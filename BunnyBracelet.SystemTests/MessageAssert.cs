using RabbitMQ.Client;

namespace BunnyBracelet.SystemTests;

internal static class MessageAssert
{
    public static void AreBodiesEqual(byte[] expected, byte[] actual)
    {
        CollectionAssert.AreEqual(expected, actual);
    }

    public static void ArePropertiesEqual(IBasicProperties expected, IBasicProperties actual)
    {
        const string IsDifferent = " is different.";

        Assert.AreEqual(expected.AppId, actual.AppId, nameof(expected.AppId) + IsDifferent);
        Assert.AreEqual(expected.ClusterId, actual.ClusterId, nameof(expected.ClusterId) + IsDifferent);
        Assert.AreEqual(expected.ContentEncoding, actual.ContentEncoding, nameof(expected.ContentEncoding) + IsDifferent);
        Assert.AreEqual(expected.ContentType, actual.ContentType, nameof(expected.ContentType) + IsDifferent);
        Assert.AreEqual(expected.CorrelationId, actual.CorrelationId, nameof(expected.CorrelationId) + IsDifferent);
        Assert.AreEqual(expected.DeliveryMode, actual.DeliveryMode, nameof(expected.DeliveryMode) + IsDifferent);
        Assert.AreEqual(expected.Expiration, actual.Expiration, nameof(expected.Expiration) + IsDifferent);
        Assert.AreEqual(expected.MessageId, actual.MessageId, nameof(expected.MessageId) + IsDifferent);
        Assert.AreEqual(expected.Persistent, actual.Persistent, nameof(expected.Persistent) + IsDifferent);
        Assert.AreEqual(expected.Priority, actual.Priority, nameof(expected.Priority) + IsDifferent);
        Assert.AreEqual(expected.ReplyTo, actual.ReplyTo, nameof(expected.ReplyTo) + IsDifferent);
        Assert.AreEqual(expected.Timestamp, actual.Timestamp, nameof(expected.Timestamp) + IsDifferent);
        Assert.AreEqual(expected.Type, actual.Type, nameof(expected.Type) + IsDifferent);
        Assert.AreEqual(expected.UserId, actual.UserId, nameof(expected.UserId) + IsDifferent);

        if (expected.ReplyToAddress is null)
        {
            Assert.IsNull(actual.ReplyToAddress, nameof(expected.ReplyToAddress) + " should be null.");
        }
        else
        {
            Assert.IsNotNull(actual.ReplyToAddress, nameof(expected.ReplyToAddress) + " should not be null.");
            ArePublicationAddressesEqual(expected.ReplyToAddress, actual.ReplyToAddress);
        }

        if (expected.Headers is null || expected.Headers.Count == 0)
        {
            Assert.AreEqual(0, actual.Headers?.Count ?? 0, nameof(expected.Headers) + " should be empty.");
        }
        else
        {
            Assert.IsNotNull(actual.Headers, nameof(expected.Headers) + " should not be null.");
            AreHeadersEqual(expected.Headers, actual.Headers);
        }
    }

    private static void AreHeadersEqual(IDictionary<string, object?> expected, IDictionary<string, object?> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count, "Headers." + nameof(expected.Count) + " is different.");

        foreach (var keyValuePair in expected)
        {
            var deserializedValue = actual[keyValuePair.Key];
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

    private static void ArePublicationAddressesEqual(PublicationAddress expected, PublicationAddress actual)
    {
        const string IsDifferent = " is different.";

        Assert.AreEqual(expected.ExchangeName, actual.ExchangeName, nameof(expected.ExchangeName) + IsDifferent);
        Assert.AreEqual(expected.ExchangeType, actual.ExchangeType, nameof(expected.ExchangeType) + IsDifferent);
        Assert.AreEqual(expected.RoutingKey, actual.RoutingKey, nameof(expected.RoutingKey) + IsDifferent);
    }
}
