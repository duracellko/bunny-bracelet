using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using RabbitMQ.Client;

namespace BunnyBracelet.Tests
{
    [TestClass]
    public class MessageSerializerTest
    {
        [TestMethod]
        public async Task WriteAndRead_EmptyMessage()
        {
            var message = default(Message);
            await TestMessageSerializationAndDeserialization(message);
        }

        [TestMethod]
        public async Task WriteAndRead_DefaultProperties()
        {
            var message = new Message(Array.Empty<byte>(), new BasicPropertiesMock());
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

            var message = new Message(Guid.NewGuid().ToByteArray(), properties);
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

            var message = new Message(Guid.NewGuid().ToByteArray(), properties);
            await TestMessageSerializationAndDeserialization(message);
        }

        [TestMethod]
        [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
        public async Task WriteAndRead_NoProperties()
        {
            var body = new byte[12345678];
            var random = new Random();
            random.NextBytes(body);

            var message = new Message(body, null);
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

            var message = new Message(null, properties);
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

            var message = new Message(null, properties);
            await TestMessageSerializationAndDeserialization(message);
        }

        [TestMethod]
        public async Task WriteAndRead_ReplyToAddress()
        {
            var properties = new BasicPropertiesMock
            {
                ReplyToAddress = new PublicationAddress(null, string.Empty, null),
            };

            var message = new Message(new byte[1000], properties);
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

            var message = new Message(new byte[1000], properties);
            await TestMessageSerializationAndDeserialization(message);
        }

        [TestMethod]
        public async Task WriteAndRead_ContentAfterEndOfMessage()
        {
            var message = CreateSampleMessage();
            var serializedMessage = await SerializeMessage(message);

            var messageBytes = new byte[serializedMessage.Length + 16];
            serializedMessage.CopyTo(messageBytes, 0);
            var tailBytes = Guid.NewGuid().ToByteArray();
            tailBytes.CopyTo(messageBytes.AsMemory().Slice(messageBytes.Length - 16));

            var deserializedMessage = await DeserializeMessage(serializedMessage);
            AssertMessagesAreEqual(message, deserializedMessage);
        }

        [TestMethod]
        public async Task Read_MissingStartByte()
        {
            var message = CreateSampleMessage();
            var serializedMessage = await SerializeMessage(message);

            var messageBytes = new byte[serializedMessage.Length - 1];
            serializedMessage.AsSpan().Slice(1).CopyTo(messageBytes);

            await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
        }

        [TestMethod]
        public async Task Read_MissingEndByte()
        {
            var message = CreateSampleMessage();
            var serializedMessage = await SerializeMessage(message);

            var messageBytes = new byte[serializedMessage.Length - 1];
            serializedMessage.AsSpan().Slice(0, serializedMessage.Length - 1).CopyTo(messageBytes);

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

            // 4 bytes = preamble, 1 byte - start properties
            // 1 bytes = property, 1 byte - MessageId property
            var newMessageIdSize = message.Body.Length * 2;
            BinaryPrimitives.WriteInt32LittleEndian(messageBytes.AsSpan().Slice(7), newMessageIdSize);

            await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
        }

        [TestMethod]
        public async Task Read_MessageIdHasNullTerminator()
        {
            var message = CreateSampleMessage();
            var messageBytes = await SerializeMessage(message);

            // 4 bytes = preamble, 1 byte - start properties,
            // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
            messageBytes[14] = 0;

            await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
        }

        [TestMethod]
        public async Task Read_MessageIdHasNullTerminatorAtStart()
        {
            var message = CreateSampleMessage();
            var messageBytes = await SerializeMessage(message);

            // 4 bytes = preamble, 1 byte - start properties,
            // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
            messageBytes[11] = 0;

            await Assert.ThrowsExceptionAsync<MessageException>(() => DeserializeMessage(messageBytes));
        }

        [TestMethod]
        public async Task WriteAndRead_MessageIdHasBrokenUnicodeLiteral()
        {
            var message = CreateSampleMessage();
            var messageBytes = await SerializeMessage(message);

            // 4 bytes = preamble, 1 byte - start properties,
            // 1 bytes = property, 1 byte - MessageId property, 4 bytes - string length
            messageBytes[11 + message.Properties!.MessageId.Length - 1] = 226;

            var deserializedMessage = await DeserializeMessage(messageBytes);

            var messageIdBuilder = new StringBuilder(message.Properties!.MessageId);
            messageIdBuilder[messageIdBuilder.Length - 1] = '\uFFFD';
            var expectedMessageId = messageIdBuilder.ToString();
            Assert.IsNotNull(deserializedMessage.Properties);
            Assert.AreEqual(expectedMessageId, deserializedMessage.Properties.MessageId);

            message.Properties.MessageId = expectedMessageId;
            AssertMessagesAreEqual(message, deserializedMessage);
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
            var message1 = new Message(Guid.NewGuid().ToByteArray(), properties);
            var message1Bytes = await SerializeMessage(message1);

            var message2 = CreateSampleMessage();
            var message2Bytes = await SerializeMessage(message2);

            // Combine bytes of message 1 and 2, but do not repeat
            // preamble (4-bytes) and message end (1 byte).
            var messageBytes = new byte[message1Bytes.Length + message2Bytes.Length - 5];
            message1Bytes.CopyTo(messageBytes, 0);

            // Overwrite last byte (end of message) of message 1.
            message2Bytes.AsMemory().Slice(4)
                .CopyTo(messageBytes.AsMemory().Slice(message1Bytes.Length - 1));

            var deserializedMessage = await DeserializeMessage(messageBytes);

            var expectedMessage = new Message(message2.Body, message1.Properties);
            expectedMessage.Properties!.MessageId = message2.Properties!.MessageId;
            expectedMessage.Properties.Timestamp = message2.Properties.Timestamp;
            expectedMessage.Properties.Type = message2.Properties.Type;

            AssertMessagesAreEqual(expectedMessage, deserializedMessage);
        }

        private static async Task TestMessageSerializationAndDeserialization(Message message)
        {
            var serializedMessage = await SerializeMessage(message);
            var deserializedMessage = await DeserializeMessage(serializedMessage);
            AssertMessagesAreEqual(message, deserializedMessage);
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

        private static void AssertMessagesAreEqual(Message message, Message deserializedMessage)
        {
            if (message.Properties is null)
            {
                Assert.IsNull(deserializedMessage.Properties, nameof(message.Properties) + " should be null.");
            }
            else
            {
                Assert.IsNotNull(deserializedMessage.Properties, nameof(message.Properties) + " should not be null.");
                AssertMessagePropertiesAreEqual(message.Properties, deserializedMessage.Properties);
            }

            CollectionAssert.AreEqual(message.Body.ToArray(), deserializedMessage.Body.ToArray(), "Body is different.");
        }

        private static void AssertMessagePropertiesAreEqual(IBasicProperties properties, IBasicProperties deserializedProperties)
        {
            const string IsDifferent = " is different.";

            Assert.AreEqual(properties.AppId, deserializedProperties.AppId, nameof(properties.AppId) + IsDifferent);
            Assert.AreEqual(properties.ClusterId, deserializedProperties.ClusterId, nameof(properties.ClusterId) + IsDifferent);
            Assert.AreEqual(properties.ContentEncoding, deserializedProperties.ContentEncoding, nameof(properties.ContentEncoding) + IsDifferent);
            Assert.AreEqual(properties.ContentType, deserializedProperties.ContentType, nameof(properties.ContentType) + IsDifferent);
            Assert.AreEqual(properties.CorrelationId, deserializedProperties.CorrelationId, nameof(properties.CorrelationId) + IsDifferent);
            Assert.AreEqual(properties.DeliveryMode, deserializedProperties.DeliveryMode, nameof(properties.DeliveryMode) + IsDifferent);
            Assert.AreEqual(properties.Expiration, deserializedProperties.Expiration, nameof(properties.Expiration) + IsDifferent);
            Assert.AreEqual(properties.MessageId, deserializedProperties.MessageId, nameof(properties.MessageId) + IsDifferent);
            Assert.AreEqual(properties.Persistent, deserializedProperties.Persistent, nameof(properties.Persistent) + IsDifferent);
            Assert.AreEqual(properties.Priority, deserializedProperties.Priority, nameof(properties.Priority) + IsDifferent);
            Assert.AreEqual(properties.ReplyTo, deserializedProperties.ReplyTo, nameof(properties.ReplyTo) + IsDifferent);
            Assert.AreEqual(properties.Timestamp, deserializedProperties.Timestamp, nameof(properties.Timestamp) + IsDifferent);
            Assert.AreEqual(properties.Type, deserializedProperties.Type, nameof(properties.Type) + IsDifferent);
            Assert.AreEqual(properties.UserId, deserializedProperties.UserId, nameof(properties.UserId) + IsDifferent);

            if (properties.ReplyToAddress is null)
            {
                Assert.IsNull(deserializedProperties.ReplyToAddress, nameof(properties.ReplyToAddress) + " should be null.");
            }
            else
            {
                Assert.IsNotNull(deserializedProperties.ReplyToAddress, nameof(properties.ReplyToAddress) + " should not be null.");
                AssertPublicationAddressesAreEqual(properties.ReplyToAddress, deserializedProperties.ReplyToAddress);
            }

            if (properties.Headers is null || properties.Headers.Count == 0)
            {
                Assert.AreEqual(0, deserializedProperties.Headers?.Count ?? 0, nameof(properties.Headers) + " should be empty.");
            }
            else
            {
                Assert.IsNotNull(deserializedProperties.Headers, nameof(properties.Headers) + " should not be null.");
                AssertMessageHeadersAreEqual(properties.Headers, deserializedProperties.Headers);
            }
        }

        private static void AssertMessageHeadersAreEqual(IDictionary<string, object?> headers, IDictionary<string, object?> deserializedHeaders)
        {
            Assert.AreEqual(headers.Count, deserializedHeaders.Count, "Headers." + nameof(headers.Count) + " is different.");

            foreach (var keyValuePair in headers)
            {
                var deserializedValue = deserializedHeaders[keyValuePair.Key];
                if (keyValuePair.Value is null)
                {
                    Assert.IsNull(deserializedValue, "Header '{0}' should be null.", keyValuePair.Key);
                }
                else
                {
                    Assert.IsNotNull(deserializedValue, "Header '{0}' should not be null.", keyValuePair.Key);
                    var valueBytes = (byte[])keyValuePair.Value;
                    Assert.IsInstanceOfType<byte[]>(deserializedValue, "Header '{0}' should be a byte array.", keyValuePair.Key);
                    var deserializedValueBytes = (byte[])deserializedValue;
                    CollectionAssert.AreEqual(valueBytes, deserializedValueBytes, "Header '{0}' has different values.", keyValuePair.Key);
                }
            }
        }

        private static void AssertPublicationAddressesAreEqual(PublicationAddress address, PublicationAddress deserializedAddress)
        {
            const string IsDifferent = " is different.";

            Assert.AreEqual(address.ExchangeName, deserializedAddress.ExchangeName, nameof(address.ExchangeName) + IsDifferent);
            Assert.AreEqual(address.ExchangeType, deserializedAddress.ExchangeType, nameof(address.ExchangeType) + IsDifferent);
            Assert.AreEqual(address.RoutingKey, deserializedAddress.RoutingKey, nameof(address.RoutingKey) + IsDifferent);
        }

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
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = (byte)(((i + 1) * (i + 52)) % 256);
            }

            return new Message(body, properties);
        }
    }
}
