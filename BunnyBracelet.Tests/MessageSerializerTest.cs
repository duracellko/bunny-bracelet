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
        [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
        public async Task WriteAndRead_Headers()
        {
            var headers = new Dictionary<string, object>()
            {
                { "Test header", Array.Empty<byte>() },
                { string.Empty, Guid.NewGuid().ToByteArray() },
                { "My header", Encoding.Unicode.GetBytes("My value") }
            };
            var properties = new BasicPropertiesMock
            {
                Headers = headers
            };

            var body = new byte[12345678];
            var random = new Random();
            random.NextBytes(body);

            var message = new Message(body, properties);
            await TestMessageSerializationAndDeserialization(message);
        }

        private static async Task TestMessageSerializationAndDeserialization(Message message)
        {
            var messageSerializer = new MessageSerializer();
            byte[] serializedMessage;

            using (var stream = new MemoryStream())
            {
                using (var messageStream = await messageSerializer.ConvertMessageToStream(message))
                {
                    await messageStream.CopyToAsync(stream);
                }

                serializedMessage = stream.ToArray();
            }

            Message deserializedMessage;
            using (var stream = new MemoryStream(serializedMessage))
            {
                deserializedMessage = await messageSerializer.ReadMessage(stream, () => new BasicPropertiesMock());
            }

            AssertMessagesAreEqual(message, deserializedMessage);
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
    }
}
