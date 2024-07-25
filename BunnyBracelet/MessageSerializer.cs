using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using RabbitMQ.Client;

namespace BunnyBracelet;

[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1124:Do not use regions", Justification = "Reading and writing is implemented in the same class.")]
public class MessageSerializer : IMessageSerializer
{
    // RMQR = RabbitMQ Relay
    private static readonly byte[] Preamble = new byte[] { 82, 77, 81, 82 };
    private static readonly Encoding TextEncoding = Encoding.UTF8;

    public async ValueTask<Message> ReadMessage(Stream stream, Func<IBasicProperties> propertiesFactory)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(propertiesFactory);

        var pipe = new Pipe();
        var writeTask = CopyStreamToPipeline(stream, pipe.Writer);
        var readTask = ReadMessage(pipe.Reader, propertiesFactory);
        await Task.WhenAll(writeTask, readTask);
        return await readTask;
    }

    public ValueTask<Stream> ConvertMessageToStream(Message message)
    {
        var pipe = new Pipe();
        WriteMessageToPipeline(message, pipe.Writer);
        return ValueTask.FromResult(pipe.Reader.AsStream());
    }

    #region ReadMessage

    private static async Task CopyStreamToPipeline(Stream stream, PipeWriter writer)
    {
        try
        {
            var finished = false;
            while (!finished)
            {
                var buffer = writer.GetMemory();
                var bytesRead = await stream.ReadAsync(buffer);
                if (bytesRead != 0)
                {
                    writer.Advance(bytesRead);
                    var result = await writer.FlushAsync();
                    if (result.IsCompleted)
                    {
                        finished = true;
                    }
                }
                else
                {
                    finished = true;
                }
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private static async Task<Message> ReadMessage(PipeReader reader, Func<IBasicProperties> propertiesFactory)
    {
        try
        {
            var readResult = await reader.ReadAtLeastAsync(4);
            var buffer = readResult.Buffer;
            buffer = ReadPreamble(buffer);
            reader.AdvanceTo(buffer.Start);

            var state = new MessageParsingState(propertiesFactory);
            while (!state.IsCompleted)
            {
                readResult = await reader.ReadAsync();
                buffer = readResult.Buffer;
                byte code = 0;
                buffer.Slice(0, 1).CopyTo(new Span<byte>(ref code));
                buffer = buffer.Slice(1);
                reader.AdvanceTo(buffer.Start);

                switch (code)
                {
                    case Codes.BasicProperties:
                        state.CreateProperties();
                        break;
                    case Codes.Property:
                        await ReadProperty(reader, state);
                        break;
                    case Codes.Header:
                        await ReadHeader(reader, state);
                        break;
                    case Codes.Body:
                        state.Body = await ReadByteArray(reader);
                        break;
                    case Codes.EndOfMesage:
                        state.IsCompleted = true;
                        break;
                    default:
                        throw new MessageException($"Unexpected code {code} reading message.");
                }
            }

            return new Message(state.Body, state.Properties);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static ReadOnlySequence<byte> ReadPreamble(ReadOnlySequence<byte> buffer)
    {
        const string ErrorMessage = "Message does not start with preamble.";

        if (buffer.Length < 4)
        {
            throw new MessageException(ErrorMessage);
        }

        Span<byte> preambleBuffer = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(preambleBuffer);
        if (preambleBuffer[0] != Preamble[0] ||
            preambleBuffer[1] != Preamble[1] ||
            preambleBuffer[2] != Preamble[2] ||
            preambleBuffer[3] != Preamble[3])
        {
            throw new MessageException(ErrorMessage);
        }

        return buffer.Slice(4);
    }

    private static async ValueTask ReadProperty(PipeReader reader, MessageParsingState state)
    {
        if (state.Properties is null)
        {
            throw new MessageException("BasicProperties code should preceed Property code.");
        }

        var readResult = await reader.ReadAsync();
        var buffer = readResult.Buffer;
        byte propertyCode = 0;
        buffer.Slice(0, 1).CopyTo(new Span<byte>(ref propertyCode));
        buffer = buffer.Slice(1);
        reader.AdvanceTo(buffer.Start);

        switch (propertyCode)
        {
            case PropertyCodes.AppId:
                state.Properties.AppId = await ReadString(reader);
                break;
            case PropertyCodes.ClusterId:
                state.Properties.ClusterId = await ReadString(reader);
                break;
            case PropertyCodes.ContentEncoding:
                state.Properties.ContentEncoding = await ReadString(reader);
                break;
            case PropertyCodes.ContentType:
                state.Properties.ContentType = await ReadString(reader);
                break;
            case PropertyCodes.CorrelationId:
                state.Properties.CorrelationId = await ReadString(reader);
                break;
            case PropertyCodes.DeliveryMode:
                state.Properties.DeliveryMode = await ReadByte(reader);
                break;
            case PropertyCodes.Expiration:
                state.Properties.Expiration = await ReadString(reader);
                break;
            case PropertyCodes.MessageId:
                state.Properties.MessageId = await ReadString(reader);
                break;
            case PropertyCodes.Persistent:
                state.Properties.Persistent = await ReadBoolean(reader);
                break;
            case PropertyCodes.Priority:
                state.Properties.Priority = await ReadByte(reader);
                break;
            case PropertyCodes.ReplyTo:
                state.Properties.ReplyTo = await ReadString(reader);
                break;
            case PropertyCodes.ReplyToAddress:
                state.Properties.ReplyToAddress = await ReadPublicationAddress(reader);
                break;
            case PropertyCodes.Timestamp:
                state.Properties.Timestamp = new AmqpTimestamp(await ReadLong(reader));
                break;
            case PropertyCodes.Type:
                state.Properties.Type = await ReadString(reader);
                break;
            case PropertyCodes.UserId:
                state.Properties.UserId = await ReadString(reader);
                break;
            default:
                throw new MessageException($"Unexpected code {propertyCode} reading message.");
        }
    }

    private static async ValueTask ReadHeader(PipeReader reader, MessageParsingState state)
    {
        if (state.Properties is null)
        {
            throw new MessageException("BasicProperties code should preceed Header code.");
        }

        var key = await ReadString(reader);
        var value = await ReadByteArray(reader);
        state.AddHeader(key, value);
    }

    private static async ValueTask<string> ReadString(PipeReader reader)
    {
        var readResult = await reader.ReadAtLeastAsync(4);
        var stringLength = GetStringLength(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(4).Start);

        readResult = await reader.ReadAtLeastAsync(stringLength);
        var result = TextEncoding.GetString(readResult.Buffer.Slice(0, stringLength));
        reader.AdvanceTo(readResult.Buffer.Slice(stringLength).Start);
        return result;

        int GetStringLength(ReadOnlySequence<byte> buffer)
        {
            Span<byte> stringLengthBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(stringLengthBytes);
            return BinaryPrimitives.ReadInt32LittleEndian(stringLengthBytes);
        }
    }

    private static async ValueTask<bool> ReadBoolean(PipeReader reader)
    {
        var readResult = await reader.ReadAsync();
        byte value = 0;
        readResult.Buffer.Slice(0, 1).CopyTo(new Span<byte>(ref value));
        reader.AdvanceTo(readResult.Buffer.Slice(1).Start);
        return value != 0;
    }

    private static async ValueTask<byte> ReadByte(PipeReader reader)
    {
        var readResult = await reader.ReadAsync();
        byte value = 0;
        readResult.Buffer.Slice(0, 1).CopyTo(new Span<byte>(ref value));
        reader.AdvanceTo(readResult.Buffer.Slice(1).Start);
        return value;
    }

    private static async ValueTask<long> ReadLong(PipeReader reader)
    {
        var readResult = await reader.ReadAtLeastAsync(8);
        var result = GetLong(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(8).Start);
        return result;

        long GetLong(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[8];
            buffer.Slice(0, 8).CopyTo(resultBytes);
            return BinaryPrimitives.ReadInt64LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<byte[]> ReadByteArray(PipeReader reader)
    {
        var readResult = await reader.ReadAtLeastAsync(4);
        var length = GetLength(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(4).Start);

        readResult = await reader.ReadAtLeastAsync(length);
        var result = new byte[length];
        readResult.Buffer.Slice(0, length).CopyTo(result);
        reader.AdvanceTo(readResult.Buffer.Slice(length).Start);
        return result;

        int GetLength(ReadOnlySequence<byte> buffer)
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lengthBytes);
            return BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        }
    }

    private static async ValueTask<PublicationAddress> ReadPublicationAddress(PipeReader reader)
    {
        var valueFlags = await ReadByte(reader);

        string? exchangeType = null;
        string? exchangeName = null;
        string? routingKey = null;

        if ((valueFlags & 1) != 0)
        {
            exchangeType = await ReadString(reader);
        }

        if ((valueFlags & 2) != 0)
        {
            exchangeName = await ReadString(reader);
        }

        if ((valueFlags & 4) != 0)
        {
            routingKey = await ReadString(reader);
        }

        return new PublicationAddress(exchangeType, exchangeName, routingKey);
    }

    #endregion

    #region ConvertMessageToStream

    private static async void WriteMessageToPipeline(Message message, PipeWriter writer)
    {
        try
        {
            WritePreamble(writer);

            if (message.Properties is not null)
            {
                await WriteProperties(message.Properties, writer);
            }

            WriteBody(writer, message.Body);
            WriteEndOfMesage(writer);
            await writer.FlushAsync();
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private static void WritePreamble(PipeWriter writer)
    {
        var buffer = writer.GetSpan(Preamble.Length);
        Preamble.CopyTo(buffer);
        writer.Advance(Preamble.Length);
    }

    private static async ValueTask WriteProperties(IBasicProperties properties, PipeWriter writer)
    {
        WriteBasicProperties(writer);
        await writer.FlushAsync();

        if (properties.IsAppIdPresent())
        {
            WriteProperty(writer, PropertyCodes.AppId, properties.AppId);
            await writer.FlushAsync();
        }

        if (properties.IsClusterIdPresent())
        {
            WriteProperty(writer, PropertyCodes.ClusterId, properties.ClusterId);
            await writer.FlushAsync();
        }

        if (properties.IsContentEncodingPresent())
        {
            WriteProperty(writer, PropertyCodes.ContentEncoding, properties.ContentEncoding);
            await writer.FlushAsync();
        }

        if (properties.IsContentTypePresent())
        {
            WriteProperty(writer, PropertyCodes.ContentType, properties.ContentType);
            await writer.FlushAsync();
        }

        if (properties.IsCorrelationIdPresent())
        {
            WriteProperty(writer, PropertyCodes.CorrelationId, properties.CorrelationId);
            await writer.FlushAsync();
        }

        if (properties.IsDeliveryModePresent())
        {
            WriteProperty(writer, PropertyCodes.DeliveryMode, properties.DeliveryMode);
            await writer.FlushAsync();
        }

        if (properties.IsExpirationPresent())
        {
            WriteProperty(writer, PropertyCodes.Expiration, properties.Expiration);
            await writer.FlushAsync();
        }

        if (properties.IsMessageIdPresent())
        {
            WriteProperty(writer, PropertyCodes.MessageId, properties.MessageId);
            await writer.FlushAsync();
        }

        WriteProperty(writer, PropertyCodes.Persistent, properties.Persistent);
        await writer.FlushAsync();

        if (properties.IsPriorityPresent())
        {
            WriteProperty(writer, PropertyCodes.Priority, properties.Priority);
            await writer.FlushAsync();
        }

        if (properties.IsReplyToPresent())
        {
            WriteProperty(writer, PropertyCodes.ReplyTo, properties.ReplyTo);
            await writer.FlushAsync();
        }

        if (properties.ReplyToAddress is not null)
        {
            WritePublicationAddress(writer, PropertyCodes.ReplyToAddress, properties.ReplyToAddress);
            await writer.FlushAsync();
        }

        if (properties.IsTimestampPresent())
        {
            WriteProperty(writer, PropertyCodes.Timestamp, properties.Timestamp.UnixTime);
            await writer.FlushAsync();
        }

        if (properties.IsTypePresent())
        {
            WriteProperty(writer, PropertyCodes.Type, properties.Type);
            await writer.FlushAsync();
        }

        if (properties.IsUserIdPresent())
        {
            WriteProperty(writer, PropertyCodes.UserId, properties.UserId);
            await writer.FlushAsync();
        }

        if (properties.IsHeadersPresent())
        {
            await WriteHeaders(properties.Headers, writer);
            await writer.FlushAsync();
        }
    }

    private static void WriteBasicProperties(PipeWriter writer)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = Codes.BasicProperties;
        writer.Advance(1);
    }

    private static void WriteProperty(PipeWriter writer, byte code, string value)
    {
        // 2 bytes = codes, 4 bytes = string length, 4 bytes per character
        // 4 bytes per character is very conservative for UTF-8
        var buffer = writer.GetSpan(2 + 4 + (value.Length * 4));

        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer = buffer.Slice(2);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, value.Length);
        buffer = buffer.Slice(4);

        var bytesCount = TextEncoding.GetBytes(value, buffer);
        writer.Advance(2 + 4 + bytesCount);
    }

    private static void WriteProperty(PipeWriter writer, byte code, bool value)
    {
        var buffer = writer.GetSpan(3);
        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer[2] = value ? (byte)1 : (byte)0;
        writer.Advance(3);
    }

    private static void WriteProperty(PipeWriter writer, byte code, byte value)
    {
        var buffer = writer.GetSpan(3);
        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer[2] = value;
        writer.Advance(3);
    }

    private static void WriteProperty(PipeWriter writer, byte code, long value)
    {
        var buffer = writer.GetSpan(10);
        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer = buffer.Slice(2);
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        writer.Advance(10);
    }

    private static void WritePublicationAddress(PipeWriter writer, byte code, PublicationAddress value)
    {
        // 2 bytes = codes, 1 byte = null flags, 4 bytes = string length, 4 bytes per character
        // 4 bytes per character is very conservative for UTF-8
        int bufferRequestSize = 3;
        byte valueFlags = 0;
        if (value.ExchangeType is not null)
        {
            bufferRequestSize += 4 + (value.ExchangeType.Length * 4);
            valueFlags |= 1;
        }

        if (value.ExchangeName is not null)
        {
            bufferRequestSize += 4 + (value.ExchangeName.Length * 4);
            valueFlags |= 2;
        }

        if (value.RoutingKey is not null)
        {
            bufferRequestSize += 4 + (value.RoutingKey.Length * 4);
            valueFlags |= 4;
        }

        var buffer = writer.GetSpan(bufferRequestSize);
        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer[2] = valueFlags;
        buffer = buffer.Slice(3);
        int bytesCount = 3;

        if (value.ExchangeType is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value.ExchangeType.Length);
            buffer = buffer.Slice(4);
            bytesCount += 4;
            var stringBytesCount = TextEncoding.GetBytes(value.ExchangeType, buffer);
            buffer = buffer.Slice(stringBytesCount);
            bytesCount += stringBytesCount;
        }

        if (value.ExchangeName is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value.ExchangeName.Length);
            buffer = buffer.Slice(4);
            bytesCount += 4;
            var stringBytesCount = TextEncoding.GetBytes(value.ExchangeName, buffer);
            buffer = buffer.Slice(stringBytesCount);
            bytesCount += stringBytesCount;
        }

        if (value.RoutingKey is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value.RoutingKey.Length);
            buffer = buffer.Slice(4);
            bytesCount += 4;
            var stringBytesCount = TextEncoding.GetBytes(value.RoutingKey, buffer);
            buffer = buffer.Slice(stringBytesCount);
            bytesCount += stringBytesCount;
        }

        writer.Advance(bytesCount);
    }

    private static async ValueTask WriteHeaders(IDictionary<string, object> headers, PipeWriter writer)
    {
        foreach (var keyValuePair in headers)
        {
            WriteHeader(writer, keyValuePair.Key, keyValuePair.Value);
            await writer.FlushAsync();
        }
    }

    private static void WriteHeader(PipeWriter writer, string key, object value)
    {
        var valueBytes = (byte[])value;

        // 1 byte = code, 4 bytes string length, 4 bytes per character, 4 bytes = value length
        var buffer = writer.GetSpan(9 + (key.Length * 4) + valueBytes.Length);
        buffer[0] = Codes.Header;
        buffer = buffer.Slice(1);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, key.Length);
        buffer = buffer.Slice(4);
        var bytesCount = TextEncoding.GetBytes(key, buffer);
        buffer = buffer.Slice(bytesCount);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, valueBytes.Length);
        buffer = buffer.Slice(4);
        valueBytes.CopyTo(buffer);

        writer.Advance(9 + bytesCount + valueBytes.Length);
    }

    private static void WriteBody(PipeWriter writer, ReadOnlyMemory<byte> body)
    {
        var buffer = writer.GetMemory(5 + body.Length);
        buffer.Span[0] = Codes.Body;
        buffer = buffer.Slice(1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, body.Length);
        buffer = buffer.Slice(4);
        body.CopyTo(buffer);
        writer.Advance(5 + body.Length);
    }

    private static void WriteEndOfMesage(PipeWriter writer)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = Codes.EndOfMesage;
        writer.Advance(1);
    }

    #endregion

    private static class Codes
    {
        public const byte EndOfMesage = 0;
        public const byte Body = 1;
        public const byte BasicProperties = 2;
        public const byte Property = 3;
        public const byte Header = 4;
    }

    private static class PropertyCodes
    {
        public const byte AppId = 1;
        public const byte ClusterId = 2;
        public const byte ContentEncoding = 3;
        public const byte ContentType = 4;
        public const byte CorrelationId = 5;
        public const byte DeliveryMode = 6;
        public const byte Expiration = 7;
        public const byte MessageId = 8;
        public const byte Persistent = 9;
        public const byte Priority = 10;
        public const byte ReplyTo = 11;
        public const byte ReplyToAddress = 12;
        public const byte Timestamp = 13;
        public const byte Type = 14;
        public const byte UserId = 15;
    }

    private sealed class MessageParsingState
    {
        private readonly Func<IBasicProperties> propertiesFactory;

        public MessageParsingState(Func<IBasicProperties> propertiesFactory)
        {
            this.propertiesFactory = propertiesFactory;
        }

        public bool IsCompleted { get; set; }

        public IBasicProperties? Properties { get; private set; }

        public ReadOnlyMemory<byte> Body { get; set; }

        public void CreateProperties()
        {
            if (Properties is null)
            {
                Properties = propertiesFactory();
            }
        }

        public void AddHeader(string key, byte[] value)
        {
            if (Properties!.Headers is null)
            {
                Properties.Headers = new Dictionary<string, object>();
            }

            Properties.Headers[key] = value;
        }
    }
}
