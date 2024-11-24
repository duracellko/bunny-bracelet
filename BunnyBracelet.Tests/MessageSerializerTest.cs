using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using RabbitMQ.Client;

namespace BunnyBracelet.Tests;

[TestClass]
public class MessageSerializerTest
{
    private static readonly DateTime TestTimestamp = new(2024, 8, 11, 23, 2, 32, 922, 129, DateTimeKind.Utc);
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Y2K38 = new(2038, 1, 19, 3, 14, 7, DateTimeKind.Unspecified);

    private static readonly Lazy<Random> Random = new(() => new Random());

    [TestMethod]
    public async Task WriteAndRead_EmptyMessage()
    {
        var message = default(Message);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_DefaultProperties()
    {
        var message = new Message(Array.Empty<byte>(), new BasicPropertiesMock(), UnixEpoch);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_SampleMessage()
    {
        var message = CreateSampleMessage();
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_PropertiesAndBody()
    {
        var properties = new BasicPropertiesMock
        {
            AppId = "Test application",
            ClusterId = "Cluster tested",
            ContentEncoding = "Unicode",
            ContentType = "application/json",
            CorrelationId = Guid.NewGuid().ToString(),
            DeliveryMode = 2,
            Expiration = "tomorrow",
            Headers = new Dictionary<string, object>(),
            MessageId = Guid.NewGuid().ToString(),
            Persistent = true,
            Priority = 129,
            ReplyTo = "return address",
            ReplyToAddress = new PublicationAddress("Topic", "return exchange", "test/channel"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Type = "Test message",
            UserId = "duracellko"
        };

        var message = new Message(GetRandomBytes(933), properties, TestTimestamp);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_Headers()
    {
        // My header: 💎, 📜, ✂
        var headers = new Dictionary<string, object>()
        {
            { "Test header", Array.Empty<byte>() },
            { string.Empty, Guid.NewGuid().ToByteArray() },
            { "My header (\uD83D\uDC8E, \uD83D\uDCDC, \u2702)", Encoding.Unicode.GetBytes("My value") }
        };
        var properties = new BasicPropertiesMock
        {
            Headers = headers
        };

        var message = new Message(GetRandomBytes(100), properties, DateTime.UtcNow);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_NoProperties()
    {
        var body = GetRandomBytes(12345678);

        var message = new Message(body, null, Y2K38);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_EmptyBody()
    {
        var headers = new Dictionary<string, object>()
        {
            { "My header", Encoding.Unicode.GetBytes("My value") }
        };
        var properties = new BasicPropertiesMock
        {
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            Headers = headers,
            Persistent = false,
            Priority = 3
        };

        var message = new Message(null, properties, UnixEpoch);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_EmptyStringHeaders()
    {
        var headers = new Dictionary<string, object>()
        {
            { string.Empty, Encoding.Unicode.GetBytes("My value") },
            { "My header", Array.Empty<byte>() }
        };
        var properties = new BasicPropertiesMock
        {
            CorrelationId = string.Empty,
            MessageId = string.Empty,
            Type = "My message",
            Headers = headers,
        };

        var message = new Message(null, properties, TestTimestamp);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_ReplyToAddress()
    {
        var properties = new BasicPropertiesMock
        {
            ReplyToAddress = new PublicationAddress(null, string.Empty, null),
        };

        var message = new Message(new byte[1000], properties, DateTime.UtcNow);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_ReplyToAddressHasUnicodeCharacters()
    {
        // ExhangeType: 💎
        // ExchangeName: ✂ > 📜
        // RoutingKey: 🦎
        var properties = new BasicPropertiesMock
        {
            ReplyToAddress = new PublicationAddress("\uD83D\uDC8E", "\u2702 > \uD83D\uDCDC", "  🦎 "),
        };

        var message = new Message(new byte[1000], properties, DateTime.UtcNow);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_HeaderContainsList()
    {
        var headers = new Dictionary<string, object>
        {
            { "test string", "My test value" },
            { "test binary", Encoding.UTF8.GetBytes("My test binary value") },
            { "test boolean", true },
            { "test byte", (byte)22 },
            { "test int16", short.MinValue },
            { "test int32", int.MinValue },
            { "test int64", long.MinValue },
            { "test uint16", ushort.MaxValue },
            { "test uint32", uint.MaxValue },
            { "test uint64", ulong.MaxValue },
            { "test single", 123.456f },
            { "test double", 987654e-56 },
            { "test decimal", 123456789.0987654321m },
            { "test timestamp", new AmqpTimestamp(DateTimeOffset.Now.ToUnixTimeSeconds()) }
        };

        var innerList = new List<object>
        {
            (byte)255,
            -1234567890123456789,
            1234567890123456789ul,
            GetRandomBytes(5000000),
            false,
            true,
            "Inner list",
            double.MaxValue,
            double.MinValue,
            double.Epsilon,
            double.E,
            default(AmqpTimestamp)
        };

        var list = new List<object>
        {
            new AmqpTimestamp(DateTimeOffset.UtcNow.AddMonths(-10).ToUnixTimeSeconds()),
            false,
            22m,
            123456e78,
            3.33e-33f,
            2024ul,
            2025u,
            (ushort)2026,
            2027L,
            2028,
            (short)2029,
            (byte)0,
            false,
            innerList,
            "Outer list",
            GetRandomBytes(80)
        };

        headers.Add("test list", list);

        var properties = new BasicPropertiesMock
        {
            Headers = headers
        };

        var message = new Message(GetRandomBytes(120), properties, TestTimestamp);
        await TestMessageSerializationAndDeserialization(message);
    }

    [TestMethod]
    public async Task WriteAndRead_ContentAfterEndOfMessage()
    {
        var message = CreateSampleMessage();
        var serializedMessage = await SerializeMessage(message);

        var messageBytes = new byte[serializedMessage.Length + 20];
        serializedMessage.CopyTo(messageBytes, 0);
        var tailBytes = GetRandomBytes(20);
        tailBytes.CopyTo(messageBytes.AsMemory()[(messageBytes.Length - 20)..]);

        var deserializedMessage = await DeserializeMessage(serializedMessage);
        MessageAssert.AreEqual(message, deserializedMessage);
    }

    [TestMethod]
    public async Task Read_MissingStartByte()
    {
        var message = CreateSampleMessage();
        var serializedMessage = await SerializeMessage(message);

        var messageBytes = new byte[serializedMessage.Length - 1];
        serializedMessage.AsSpan()[1..].CopyTo(messageBytes);

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_MissingEndByte()
    {
        var message = CreateSampleMessage();
        var serializedMessage = await SerializeMessage(message);

        var messageBytes = new byte[serializedMessage.Length - 1];
        serializedMessage.AsSpan()[..(serializedMessage.Length - 1)].CopyTo(messageBytes);

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_BodyShorterThanBodySize()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // Bytes from end: 1 byte = message end, body length, 4 bytes = body size
        messageBytes[messageBytes.Length - message.Body.Length - 5] += 1;

        // Check there was no overflow
        Assert.AreNotEqual(0, messageBytes[messageBytes.Length - message.Body.Length - 5]);

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_BodyLongerThanBodySize()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // Bytes from end: 1 byte = message end, body length, 4 bytes = body size
        messageBytes[messageBytes.Length - message.Body.Length - 5] -= 2;

        // Check there was no overflow
        Assert.IsTrue(messageBytes[messageBytes.Length - message.Body.Length - 5] < 254);

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_MessageIdSizeIsLargerThanMessage()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // 4 bytes = preamble, 8 bytes - timestamp, 1 byte - start properties
        // 1 bytes = property, 1 byte - MessageId property
        var newMessageIdSize = message.Body.Length * 2;
        BinaryPrimitives.WriteInt32LittleEndian(messageBytes.AsSpan()[15..], newMessageIdSize);

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_MessageIdHasNullTerminator()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // 4 bytes = preamble, 8 bytes - timestamp, 1 byte - start properties,
        // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
        messageBytes[22] = 0;

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task Read_MessageIdHasNullTerminatorAtStart()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // 4 bytes = preamble, 8 bytes - timestamp, 1 byte - start properties,
        // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
        messageBytes[19] = 0;

        await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
    }

    [TestMethod]
    public async Task WriteAndRead_MessageIdHasBrokenUnicodeLiteral()
    {
        var message = CreateSampleMessage();
        var messageBytes = await SerializeMessage(message);

        // 4 bytes = preamble, 8 bytes - timestamp, 1 byte - start properties,
        // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
        messageBytes[19 + message.Properties!.MessageId.Length - 1] = 226;

        var deserializedMessage = await DeserializeMessage(messageBytes);

        var messageIdBuilder = new StringBuilder(message.Properties!.MessageId);
        messageIdBuilder[^1] = '\uFFFD';
        var expectedMessageId = messageIdBuilder.ToString();
        Assert.IsNotNull(deserializedMessage.Properties);
        Assert.AreEqual(expectedMessageId, deserializedMessage.Properties.MessageId);

        message.Properties.MessageId = expectedMessageId;
        MessageAssert.AreEqual(message, deserializedMessage);
    }

    [TestMethod]
    public async Task WriteAndRead_PropertiesAndBodyAreDuplicatedWithDifferentValues()
    {
        var properties = new BasicPropertiesMock
        {
            AppId = "Test application",
            ClusterId = "Cluster tested",
            ContentEncoding = "Unicode",
            ContentType = "application/json",
            CorrelationId = Guid.NewGuid().ToString(),
            DeliveryMode = 2,
            Expiration = "tomorrow",
            Headers = new Dictionary<string, object>(),
            MessageId = Guid.NewGuid().ToString(),
            Persistent = true,
            Priority = 129,
            ReplyTo = "return address",
            ReplyToAddress = new PublicationAddress("Topic", "return exchange", "test/channel"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Type = "Test message",
            UserId = "duracellko"
        };
        var message1 = new Message(GetRandomBytes(1342), properties, TestTimestamp);
        var message1Bytes = await SerializeMessage(message1);

        var message2 = CreateSampleMessage();
        var message2Bytes = await SerializeMessage(message2);

        // Combine bytes of message 1 and 2, but do not repeat
        // preamble (4-bytes), timestamp and message end (1 byte).
        var messageBytes = new byte[message1Bytes.Length + message2Bytes.Length - 13];
        message1Bytes.CopyTo(messageBytes, 0);

        // Overwrite last byte (end of message) of message 1.
        message2Bytes.AsMemory()[12..]
            .CopyTo(messageBytes.AsMemory()[(message1Bytes.Length - 1)..]);

        var deserializedMessage = await DeserializeMessage(messageBytes);

        var expectedMessage = new Message(message2.Body, message1.Properties, TestTimestamp);
        expectedMessage.Properties!.MessageId = message2.Properties!.MessageId;
        expectedMessage.Properties.Timestamp = message2.Properties.Timestamp;
        expectedMessage.Properties.Type = message2.Properties.Type;

        MessageAssert.AreEqual(expectedMessage, deserializedMessage);
    }

    [TestMethod]
    public async Task WriteAndRead_NoHeadersButIsHeadersPresentIsTrue()
    {
        var properties = new BasicPropertiesMock
        {
            OverrideIsHeadersPresent = true
        };
        const string text = """
            When publishing a message via RabbitMQ Management page,
            then the message has IsHeadersPresent set to true,
            but Headers property is null.
            """;
        var body = Encoding.Unicode.GetBytes(text);

        var message = new Message(body, properties, TestTimestamp);
        await TestMessageSerializationAndDeserialization(message);
    }

    private static async Task TestMessageSerializationAndDeserialization(Message message)
    {
        var serializedMessage = await SerializeMessage(message);
        var deserializedMessage = await DeserializeMessage(serializedMessage);
        MessageAssert.AreEqual(message, deserializedMessage);
    }

    private static async Task<byte[]> SerializeMessage(Message message)
    {
        var messageSerializer = new MessageSerializer();
        using var stream = new MemoryStream();
        using var messageStream = await messageSerializer.ConvertMessageToStream(message);
        await messageStream.CopyToAsync(stream);
        return stream.ToArray();
    }

    private static async Task<Message> DeserializeMessage(byte[] messageBytes)
    {
        var messageSerializer = new MessageSerializer();
        using var stream = new MemoryStream(messageBytes);
        return await messageSerializer.ReadMessage(stream, () => new BasicPropertiesMock());
    }

    [SuppressMessage("Style", "IDE0047:Remove unnecessary parentheses", Justification = "Keep parentheses for modulo.")]
    private static Message CreateSampleMessage()
    {
        // Type: Test message (💎, 📜, ✂, 🦎, 🖖)
        var properties = new BasicPropertiesMock
        {
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Type = "Test message (\uD83D\uDC8E, \uD83D\uDCDC, \u2702, \uD83E\uDD8E, \uD83D\uDD96)",
        };

        var body = new byte[4000];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(((i + 1) * (i + 52)) % 256);
        }

        return new Message(body, properties, DateTime.UtcNow);
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
    private static byte[] GetRandomBytes(int length)
    {
        var result = new byte[length];
        Random.Value.NextBytes(result);
        return result;
    }
}
