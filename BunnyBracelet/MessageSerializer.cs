using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using RabbitMQ.Client;

namespace BunnyBracelet;

/// <summary>
/// This object can deserialize a <see cref="Message"/> from a Stream or
/// serialize a Message to Stream.
/// </summary>
/// <remarks>
/// Serialized message is a binary format that always starts with RMQR encoded in ASCII and
/// ends with byte 0. It contains timestamp followed by sequence of sections and each section
/// stores specific part of the message, e.g. basic property or body content.
/// First byte of each section identifies type of section and and what part of the message
/// is stored in the section.
///
/// All multi-byte values are stored in little-endian order.
/// All strings are encoded in UTF-8 and starts with integer that specifies number of bytes
/// used for the string. Strings containing null-terminating character 0x00 are not allowed.
/// </remarks>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1124:Do not use regions", Justification = "Reading and writing is implemented in the same class.")]
public class MessageSerializer : IMessageSerializer
{
    // RMQR = RabbitMQ Relay
    private static readonly byte[] Preamble = [82, 77, 81, 82];
    private static readonly Encoding TextEncoding = Encoding.UTF8;

    public async ValueTask<Message> ReadMessage(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var pipe = new Pipe();
        var writeTask = CopyStreamToPipeline(stream, pipe.Writer, cancellationToken);
        var readTask = ReadMessage(pipe.Reader, cancellationToken);
        await Task.WhenAll(writeTask, readTask);
        return await readTask;
    }

    public ValueTask<Stream> ConvertMessageToStream(Message message, CancellationToken cancellationToken = default)
    {
        var pipe = new Pipe();
        WriteMessageToPipeline(message, pipe.Writer, cancellationToken);
        return ValueTask.FromResult(pipe.Reader.AsStream());
    }

    #region ReadMessage

    private static async Task CopyStreamToPipeline(Stream stream, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            var finished = false;
            while (!finished)
            {
                var buffer = writer.GetMemory();
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead != 0)
                {
                    writer.Advance(bytesRead);
                    var result = await writer.FlushAsync(cancellationToken);
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

    private static async Task<Message> ReadMessage(PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            var readResult = await reader.ReadAtLeastAsync(4, cancellationToken);
            var buffer = readResult.Buffer;
            buffer = ReadPreamble(buffer);
            reader.AdvanceTo(buffer.Start);

            var state = new MessageParsingState();
            await ReadTimestamp(reader, state, cancellationToken);

            while (!state.IsCompleted)
            {
                var code = await ReadByte(reader, cancellationToken);

                switch (code)
                {
                    case Codes.BasicProperties:
                        state.CreateProperties();
                        break;
                    case Codes.Property:
                        await ReadProperty(reader, state, cancellationToken);
                        break;
                    case Codes.Header:
                        await ReadHeader(reader, state, cancellationToken);
                        break;
                    case Codes.Body:
                        state.Body = await ReadByteArray(reader, cancellationToken);
                        break;
                    case Codes.EndOfMesage:
                        state.IsCompleted = true;
                        break;
                    default:
                        throw new MessageException($"Unexpected code {code} reading message.");
                }
            }

            return new Message(state.Body, state.Properties, state.Timestamp);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new MessageException("Unexpected end of stream.", ex);
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

    private static async ValueTask ReadTimestamp(PipeReader reader, MessageParsingState state, CancellationToken cancellationToken)
    {
        var timestampBinary = await ReadInt64(reader, cancellationToken);
        if (timestampBinary < 0)
        {
            throw new MessageException("Timestamp should be in UTC, but local time zone bit was detected.");
        }

        state.Timestamp = DateTime.FromBinary(timestampBinary);
    }

    private static async ValueTask ReadProperty(PipeReader reader, MessageParsingState state, CancellationToken cancellationToken)
    {
        if (state.Properties is null)
        {
            throw new MessageException("BasicProperties code should preceed Property code.");
        }

        var propertyCode = await ReadByte(reader, cancellationToken);

        switch (propertyCode)
        {
            case PropertyCodes.AppId:
                state.Properties.AppId = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.ClusterId:
                state.Properties.ClusterId = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.ContentEncoding:
                state.Properties.ContentEncoding = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.ContentType:
                state.Properties.ContentType = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.CorrelationId:
                state.Properties.CorrelationId = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.DeliveryMode:
                state.Properties.DeliveryMode = (DeliveryModes)await ReadByte(reader, cancellationToken);
                break;
            case PropertyCodes.Expiration:
                state.Properties.Expiration = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.MessageId:
                state.Properties.MessageId = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.Priority:
                state.Properties.Priority = await ReadByte(reader, cancellationToken);
                break;
            case PropertyCodes.ReplyTo:
                state.Properties.ReplyTo = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.ReplyToAddress:
                state.Properties.ReplyToAddress = await ReadPublicationAddress(reader, cancellationToken);
                break;
            case PropertyCodes.Timestamp:
                state.Properties.Timestamp = await ReadTimestamp(reader, cancellationToken);
                break;
            case PropertyCodes.Type:
                state.Properties.Type = await ReadString(reader, cancellationToken);
                break;
            case PropertyCodes.UserId:
                state.Properties.UserId = await ReadString(reader, cancellationToken);
                break;
            default:
                throw new MessageException($"Unexpected code {propertyCode} reading message.");
        }
    }

    private static async ValueTask ReadHeader(PipeReader reader, MessageParsingState state, CancellationToken cancellationToken)
    {
        if (state.Properties is null)
        {
            throw new MessageException("BasicProperties code should preceed Header code.");
        }

        var key = await ReadString(reader, cancellationToken);
        var value = await ReadHeaderValue(reader, cancellationToken);
        state.AddHeader(key, value);
    }

    private static async ValueTask<object> ReadHeaderValue(PipeReader reader, CancellationToken cancellationToken)
    {
        var valueTypeCode = await ReadByte(reader, cancellationToken);
        return valueTypeCode switch
        {
            ValueTypeCodes.ByteArray => await ReadByteArray(reader, cancellationToken),
            ValueTypeCodes.Boolean => await ReadBoolean(reader, cancellationToken),
            ValueTypeCodes.Byte => await ReadByte(reader, cancellationToken),
            ValueTypeCodes.Int16 => await ReadInt16(reader, cancellationToken),
            ValueTypeCodes.Int32 => await ReadInt32(reader, cancellationToken),
            ValueTypeCodes.Int64 => await ReadInt64(reader, cancellationToken),
            ValueTypeCodes.UInt16 => await ReadUInt16(reader, cancellationToken),
            ValueTypeCodes.UInt32 => await ReadUInt32(reader, cancellationToken),
            ValueTypeCodes.UInt64 => await ReadUInt64(reader, cancellationToken),
            ValueTypeCodes.Single => await ReadSingle(reader, cancellationToken),
            ValueTypeCodes.Double => await ReadDouble(reader, cancellationToken),
            ValueTypeCodes.Decimal => await ReadDecimal(reader, cancellationToken),
            ValueTypeCodes.String => await ReadString(reader, cancellationToken),
            ValueTypeCodes.Timestamp => await ReadTimestamp(reader, cancellationToken),
            ValueTypeCodes.List => await ReadList(reader, cancellationToken),
            _ => throw new MessageException($"Unexpected header value type code {valueTypeCode}."),
        };
    }

    private static async ValueTask<string> ReadString(PipeReader reader, CancellationToken cancellationToken)
    {
        const byte nullTerminator = 0;

        var stringLength = await ReadInt32(reader, cancellationToken);
        var readResult = await reader.ReadAtLeastAsync(stringLength, cancellationToken);
        var stringBuffer = readResult.Buffer.Slice(0, stringLength);

        if (stringBuffer.PositionOf(nullTerminator).HasValue)
        {
            throw new MessageException("A string inside the message contains unallowed null terminator character (0).");
        }

        var result = TextEncoding.GetString(stringBuffer);
        reader.AdvanceTo(readResult.Buffer.Slice(stringLength).Start);
        return result;
    }

    private static async ValueTask<bool> ReadBoolean(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAsync(cancellationToken);
        byte value = 0;
        readResult.Buffer.Slice(0, 1).CopyTo(new Span<byte>(ref value));
        reader.AdvanceTo(readResult.Buffer.Slice(1).Start);
        return value != 0;
    }

    private static async ValueTask<byte> ReadByte(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAsync(cancellationToken);
        byte value = 0;
        readResult.Buffer.Slice(0, 1).CopyTo(new Span<byte>(ref value));
        reader.AdvanceTo(readResult.Buffer.Slice(1).Start);
        return value;
    }

    private static async ValueTask<short> ReadInt16(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(2, cancellationToken);
        var result = GetInt16(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(2).Start);
        return result;

        static short GetInt16(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[2];
            buffer.Slice(0, 2).CopyTo(resultBytes);
            return BinaryPrimitives.ReadInt16LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<int> ReadInt32(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(4, cancellationToken);
        var result = GetInt32(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(4).Start);
        return result;

        static int GetInt32(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(resultBytes);
            return BinaryPrimitives.ReadInt32LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<long> ReadInt64(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(8, cancellationToken);
        var result = GetInt64(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(8).Start);
        return result;

        static long GetInt64(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[8];
            buffer.Slice(0, 8).CopyTo(resultBytes);
            return BinaryPrimitives.ReadInt64LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<ushort> ReadUInt16(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(2, cancellationToken);
        var result = GetUInt16(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(2).Start);
        return result;

        static ushort GetUInt16(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[2];
            buffer.Slice(0, 2).CopyTo(resultBytes);
            return BinaryPrimitives.ReadUInt16LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<uint> ReadUInt32(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(4, cancellationToken);
        var result = GetUInt32(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(4).Start);
        return result;

        static uint GetUInt32(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(resultBytes);
            return BinaryPrimitives.ReadUInt32LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<ulong> ReadUInt64(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(8, cancellationToken);
        var result = GetUInt64(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(8).Start);
        return result;

        static ulong GetUInt64(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[8];
            buffer.Slice(0, 8).CopyTo(resultBytes);
            return BinaryPrimitives.ReadUInt64LittleEndian(resultBytes);
        }
    }

    private static async ValueTask<float> ReadSingle(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(4, cancellationToken);
        var result = GetSingle(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(4).Start);
        return result;

        static float GetSingle(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(resultBytes);
            return BinaryPrimitives.ReadSingleLittleEndian(resultBytes);
        }
    }

    private static async ValueTask<double> ReadDouble(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(8, cancellationToken);
        var result = GetDouble(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(8).Start);
        return result;

        static double GetDouble(ReadOnlySequence<byte> buffer)
        {
            Span<byte> resultBytes = stackalloc byte[8];
            buffer.Slice(0, 8).CopyTo(resultBytes);
            return BinaryPrimitives.ReadDoubleLittleEndian(resultBytes);
        }
    }

    private static async ValueTask<decimal> ReadDecimal(PipeReader reader, CancellationToken cancellationToken)
    {
        var readResult = await reader.ReadAtLeastAsync(16, cancellationToken);
        var result = ReadDecimal(readResult.Buffer);
        reader.AdvanceTo(readResult.Buffer.Slice(16).Start);
        return result;

        static decimal ReadDecimal(ReadOnlySequence<byte> buffer)
        {
            Span<byte> bytesBuffer = stackalloc byte[4];
            Span<int> intBuffer = stackalloc int[4];

            buffer.Slice(0, 4).CopyTo(bytesBuffer);
            intBuffer[0] = BinaryPrimitives.ReadInt32LittleEndian(bytesBuffer);
            buffer = buffer.Slice(4);
            buffer.Slice(0, 4).CopyTo(bytesBuffer);
            intBuffer[1] = BinaryPrimitives.ReadInt32LittleEndian(bytesBuffer);
            buffer = buffer.Slice(4);
            buffer.Slice(0, 4).CopyTo(bytesBuffer);
            intBuffer[2] = BinaryPrimitives.ReadInt32LittleEndian(bytesBuffer);
            buffer = buffer.Slice(4);
            buffer.Slice(0, 4).CopyTo(bytesBuffer);
            intBuffer[3] = BinaryPrimitives.ReadInt32LittleEndian(bytesBuffer);

            return new decimal(intBuffer);
        }
    }

    private static async ValueTask<byte[]> ReadByteArray(PipeReader reader, CancellationToken cancellationToken)
    {
        var length = await ReadInt32(reader, cancellationToken);
        var readResult = await reader.ReadAtLeastAsync(length, cancellationToken);
        var result = new byte[length];
        readResult.Buffer.Slice(0, length).CopyTo(result);
        reader.AdvanceTo(readResult.Buffer.Slice(length).Start);
        return result;
    }

    private static async ValueTask<AmqpTimestamp> ReadTimestamp(PipeReader reader, CancellationToken cancellationToken)
    {
        return new AmqpTimestamp(await ReadInt64(reader, cancellationToken));
    }

    private static async ValueTask<IReadOnlyList<object>> ReadList(PipeReader reader, CancellationToken cancellationToken)
    {
        var itemsCount = await ReadInt32(reader, cancellationToken);
        var result = new List<object>(itemsCount);

        for (var i = 0; i < itemsCount; i++)
        {
            result.Add(await ReadHeaderValue(reader, cancellationToken));
        }

        return result;
    }

    private static async ValueTask<PublicationAddress> ReadPublicationAddress(PipeReader reader, CancellationToken cancellationToken)
    {
        var valueFlags = await ReadByte(reader, cancellationToken);

        string? exchangeType = null;
        string? exchangeName = null;
        string? routingKey = null;

        if ((valueFlags & 1) != 0)
        {
            exchangeType = await ReadString(reader, cancellationToken);
        }

        if ((valueFlags & 2) != 0)
        {
            exchangeName = await ReadString(reader, cancellationToken);
        }

        if ((valueFlags & 4) != 0)
        {
            routingKey = await ReadString(reader, cancellationToken);
        }

        return new PublicationAddress(exchangeType ?? string.Empty, exchangeName ?? string.Empty, routingKey ?? string.Empty);
    }

    #endregion

    #region ConvertMessageToStream

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "All exceptions are reported to reader.")]
    private static async void WriteMessageToPipeline(Message message, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            WritePreamble(writer);
            WriteTimestamp(writer, message.Timestamp);

            if (message.Properties is not null)
            {
                await WriteProperties(message.Properties, writer, cancellationToken);
            }

            WriteBody(writer, message.Body);
            WriteEndOfMesage(writer);
            await writer.FlushAsync(cancellationToken);
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
    }

    private static void WritePreamble(PipeWriter writer)
    {
        var buffer = writer.GetSpan(Preamble.Length);
        Preamble.CopyTo(buffer);
        writer.Advance(Preamble.Length);
    }

    private static void WriteTimestamp(PipeWriter writer, DateTime timestamp)
    {
        WriteInt64(writer, timestamp.ToBinary());
    }

    private static async ValueTask WriteProperties(IReadOnlyBasicProperties properties, PipeWriter writer, CancellationToken cancellationToken)
    {
        WriteBasicProperties(writer);
        await writer.FlushAsync(cancellationToken);

        if (properties.IsAppIdPresent())
        {
            WriteProperty(writer, PropertyCodes.AppId, properties.AppId);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsClusterIdPresent())
        {
            WriteProperty(writer, PropertyCodes.ClusterId, properties.ClusterId);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsContentEncodingPresent())
        {
            WriteProperty(writer, PropertyCodes.ContentEncoding, properties.ContentEncoding);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsContentTypePresent())
        {
            WriteProperty(writer, PropertyCodes.ContentType, properties.ContentType);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsCorrelationIdPresent())
        {
            WriteProperty(writer, PropertyCodes.CorrelationId, properties.CorrelationId);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsDeliveryModePresent())
        {
            WriteProperty(writer, PropertyCodes.DeliveryMode, (byte)properties.DeliveryMode);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsExpirationPresent())
        {
            WriteProperty(writer, PropertyCodes.Expiration, properties.Expiration);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsMessageIdPresent())
        {
            WriteProperty(writer, PropertyCodes.MessageId, properties.MessageId);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsPriorityPresent())
        {
            WriteProperty(writer, PropertyCodes.Priority, properties.Priority);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsReplyToPresent())
        {
            WriteProperty(writer, PropertyCodes.ReplyTo, properties.ReplyTo);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.ReplyToAddress is not null)
        {
            WritePublicationAddress(writer, PropertyCodes.ReplyToAddress, properties.ReplyToAddress);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsTimestampPresent())
        {
            WriteProperty(writer, PropertyCodes.Timestamp, properties.Timestamp.UnixTime);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsTypePresent())
        {
            WriteProperty(writer, PropertyCodes.Type, properties.Type);
            await writer.FlushAsync(cancellationToken);
        }

        if (properties.IsUserIdPresent())
        {
            WriteProperty(writer, PropertyCodes.UserId, properties.UserId);
            await writer.FlushAsync(cancellationToken);
        }

        // When publishing a message via RabbitMQ Management page,
        // then the message has IsHeadersPresent set to true, but Headers property is null.
        if (properties.IsHeadersPresent() && properties.Headers is not null)
        {
            await WriteHeaders(properties.Headers, writer, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }

    private static void WriteBasicProperties(PipeWriter writer)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = Codes.BasicProperties;
        writer.Advance(1);
    }

    private static void WriteProperty(PipeWriter writer, byte code, string? value)
    {
        if (value != null)
        {
            var buffer = writer.GetSpan(2);
            buffer[0] = Codes.Property;
            buffer[1] = code;
            writer.Advance(2);

            WriteString(writer, value);
        }
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
        buffer = buffer[2..];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        writer.Advance(10);
    }

    private static void WritePublicationAddress(PipeWriter writer, byte code, PublicationAddress value)
    {
        byte valueFlags = 0;
        if (value.ExchangeType is not null)
        {
            valueFlags |= 1;
        }

        if (value.ExchangeName is not null)
        {
            valueFlags |= 2;
        }

        if (value.RoutingKey is not null)
        {
            valueFlags |= 4;
        }

        var buffer = writer.GetSpan(3);
        buffer[0] = Codes.Property;
        buffer[1] = code;
        buffer[2] = valueFlags;
        writer.Advance(3);

        if (value.ExchangeType is not null)
        {
            WriteString(writer, value.ExchangeType);
        }

        if (value.ExchangeName is not null)
        {
            WriteString(writer, value.ExchangeName);
        }

        if (value.RoutingKey is not null)
        {
            WriteString(writer, value.RoutingKey);
        }
    }

    private static async ValueTask WriteHeaders(IDictionary<string, object?> headers, PipeWriter writer, CancellationToken cancellationToken)
    {
        foreach (var keyValuePair in headers)
        {
            if (keyValuePair.Value is not null)
            {
                WriteHeader(writer, keyValuePair.Key, keyValuePair.Value);
                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static void WriteHeader(PipeWriter writer, string key, object value)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = Codes.Header;
        writer.Advance(1);

        WriteString(writer, key);
        WriteHeaderValue(writer, value);
    }

    private static void WriteHeaderValue(PipeWriter writer, object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Most likely type is string, but that is encoded as array.
        // So it is checked first.
        if (value is byte[] byteArrayValue)
        {
            WriteByte(writer, ValueTypeCodes.ByteArray);
            WriteByteArray(writer, byteArrayValue);
        }
        else if (value is bool booleanValue)
        {
            WriteByte(writer, ValueTypeCodes.Boolean);
            WriteBoolean(writer, booleanValue);
        }
        else if (value is byte byteValue)
        {
            WriteByte(writer, ValueTypeCodes.Byte);
            WriteByte(writer, byteValue);
        }
        else if (value is short int16Value)
        {
            WriteByte(writer, ValueTypeCodes.Int16);
            WriteInt16(writer, int16Value);
        }
        else if (value is int int32Value)
        {
            WriteByte(writer, ValueTypeCodes.Int32);
            WriteInt32(writer, int32Value);
        }
        else if (value is long int64Value)
        {
            WriteByte(writer, ValueTypeCodes.Int64);
            WriteInt64(writer, int64Value);
        }
        else if (value is ushort uint16Value)
        {
            WriteByte(writer, ValueTypeCodes.UInt16);
            WriteUInt16(writer, uint16Value);
        }
        else if (value is uint uint32Value)
        {
            WriteByte(writer, ValueTypeCodes.UInt32);
            WriteUInt32(writer, uint32Value);
        }
        else if (value is ulong uint64Value)
        {
            WriteByte(writer, ValueTypeCodes.UInt64);
            WriteUInt64(writer, uint64Value);
        }
        else if (value is float singleValue)
        {
            WriteByte(writer, ValueTypeCodes.Single);
            WriteSingle(writer, singleValue);
        }
        else if (value is double doubleValue)
        {
            WriteByte(writer, ValueTypeCodes.Double);
            WriteDouble(writer, doubleValue);
        }
        else if (value is decimal decimalValue)
        {
            WriteByte(writer, ValueTypeCodes.Decimal);
            WriteDecimal(writer, decimalValue);
        }
        else if (value is string stringValue)
        {
            WriteByte(writer, ValueTypeCodes.String);
            WriteString(writer, stringValue);
        }
        else if (value is AmqpTimestamp timestampValue)
        {
            WriteByte(writer, ValueTypeCodes.Timestamp);
            WriteTimestamp(writer, timestampValue);
        }
        else if (value is IReadOnlyList<object> listValue)
        {
            WriteByte(writer, ValueTypeCodes.List);
            WriteList(writer, listValue);
        }
        else
        {
            throw new MessageException($"Unsupported header value type '{value.GetType()}'");
        }
    }

    private static void WriteBody(PipeWriter writer, ReadOnlyMemory<byte> body)
    {
        // 1 byte = code, 4 bytes body length
        var buffer = writer.GetMemory(5 + body.Length);
        buffer.Span[0] = Codes.Body;
        buffer = buffer[1..];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, body.Length);
        buffer = buffer[4..];
        body.CopyTo(buffer);
        writer.Advance(5 + body.Length);
    }

    private static void WriteEndOfMesage(PipeWriter writer)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = Codes.EndOfMesage;
        writer.Advance(1);
    }

    private static void WriteBoolean(PipeWriter writer, bool value)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = value ? (byte)255 : (byte)0;
        writer.Advance(1);
    }

    private static void WriteByte(PipeWriter writer, byte value)
    {
        var buffer = writer.GetSpan(1);
        buffer[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt16(PipeWriter writer, short value)
    {
        var buffer = writer.GetSpan(2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        writer.Advance(2);
    }

    private static void WriteInt32(PipeWriter writer, int value)
    {
        var buffer = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        writer.Advance(4);
    }

    private static void WriteInt64(PipeWriter writer, long value)
    {
        var buffer = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        writer.Advance(8);
    }

    private static void WriteUInt16(PipeWriter writer, ushort value)
    {
        var buffer = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        writer.Advance(2);
    }

    private static void WriteUInt32(PipeWriter writer, uint value)
    {
        var buffer = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        writer.Advance(4);
    }

    private static void WriteUInt64(PipeWriter writer, ulong value)
    {
        var buffer = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        writer.Advance(8);
    }

    private static void WriteSingle(PipeWriter writer, float value)
    {
        var buffer = writer.GetSpan(4);
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        writer.Advance(4);
    }

    private static void WriteDouble(PipeWriter writer, double value)
    {
        var buffer = writer.GetSpan(8);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        writer.Advance(8);
    }

    private static void WriteDecimal(PipeWriter writer, decimal value)
    {
        var buffer = writer.GetSpan(16);
        Span<int> intBuffer = stackalloc int[4];
        decimal.GetBits(value, intBuffer);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, intBuffer[0]);
        buffer = buffer[4..];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, intBuffer[1]);
        buffer = buffer[4..];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, intBuffer[2]);
        buffer = buffer[4..];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, intBuffer[3]);

        writer.Advance(16);
    }

    private static void WriteString(PipeWriter writer, string value)
    {
        // 4 bytes per character is very conservative for UTF-8
        var buffer = writer.GetSpan(4 + (value.Length * 4));

        var lengthBuffer = buffer[..4];
        var bytesCount = TextEncoding.GetBytes(value, buffer[4..]);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, bytesCount);

        writer.Advance(bytesCount + 4);
    }

    private static void WriteByteArray(PipeWriter writer, byte[] value)
    {
        var buffer = writer.GetSpan(4 + value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value.Length);
        buffer = buffer[4..];
        value.CopyTo(buffer);
        writer.Advance(4 + value.Length);
    }

    private static void WriteTimestamp(PipeWriter writer, AmqpTimestamp value)
    {
        WriteInt64(writer, value.UnixTime);
    }

    private static void WriteList(PipeWriter writer, IReadOnlyList<object> valueList)
    {
        WriteInt32(writer, valueList.Count);

        foreach (var value in valueList)
        {
            WriteHeaderValue(writer, value);
        }
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
        public const byte Priority = 9;
        public const byte ReplyTo = 10;
        public const byte ReplyToAddress = 11;
        public const byte Timestamp = 12;
        public const byte Type = 13;
        public const byte UserId = 14;
    }

    private static class ValueTypeCodes
    {
        public const byte Boolean = 1;
        public const byte Byte = 2;
        public const byte Int16 = 3;
        public const byte Int32 = 4;
        public const byte Int64 = 5;
        public const byte UInt16 = 6;
        public const byte UInt32 = 7;
        public const byte UInt64 = 8;
        public const byte Single = 9;
        public const byte Double = 10;
        public const byte Decimal = 11;
        public const byte ByteArray = 12;
        public const byte String = 13;
        public const byte Timestamp = 14;
        public const byte List = 15;
    }

    private sealed class MessageParsingState
    {
        public bool IsCompleted { get; set; }

        public BasicProperties? Properties { get; private set; }

        public ReadOnlyMemory<byte> Body { get; set; }

        public DateTime Timestamp { get; set; }

        public void CreateProperties()
        {
            Properties ??= new BasicProperties();
        }

        public void AddHeader(string key, object? value)
        {
            System.Diagnostics.Debug.Assert(Properties is not null, "CreateProperties is called before AddHeader.");

            Properties.Headers ??= new Dictionary<string, object?>();
            Properties.Headers[key] = value;
        }
    }
}
