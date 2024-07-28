using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using RabbitMQ.Client;

#pragma warning disable SA1008 // Opening parenthesis should be spaced correctly
using RabbitMessage = (RabbitMQ.Client.IBasicProperties? properties, byte[] body);
#pragma warning restore SA1008 // Opening parenthesis should be spaced correctly

namespace BunnyBracelet.SystemTests;

[TestClass]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Creating test data is not performance critical.")]
public class SystemTest
{
    private const string OutputSeparator = "----------";

    [TestMethod]
    public async Task SendMessageBetween2RabbitMQInstances()
    {
        await using var rabbit1 = new RabbitRunner(5673);
        await using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);

        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();

            using var connection2 = rabbit2.CreateConnection();
            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            var headers = new Dictionary<string, object>()
            {
                { "Test header", Array.Empty<byte>() },
                { string.Empty, Guid.NewGuid().ToByteArray() },
                { "My header (\uD83D\uDC8E, \uD83D\uDCDC, \u2702)", Encoding.Unicode.GetBytes("My value") }
            };

            var properties = connection1.CreateProperties();
            properties.AppId = "Test app (\uD83D\uDC8E, \uD83D\uDCDC, \u2702, \uD83E\uDD8E, \uD83D\uDD96)";
            properties.ClusterId = "Cluster tested";
            properties.ContentEncoding = "Unicode";
            properties.ContentType = "application/json";
            properties.CorrelationId = Guid.NewGuid().ToString();
            properties.DeliveryMode = 1;
            properties.Expiration = "60000";
            properties.Headers = headers;
            properties.MessageId = Guid.NewGuid().ToString();
            properties.Persistent = false;
            properties.Priority = 129;
            properties.ReplyTo = "return address";
            properties.ReplyToAddress = new PublicationAddress("Topic", "return exchange", "test/channel");
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            properties.Type = "Test message";
            properties.UserId = "bunny";

            var messageContent = Guid.NewGuid().ToByteArray();
            connection1.Publish(bunny1.OutboundExchange.Name!, properties, messageContent);

            var messageResult = await WaitForMessage(queue2);

            Assert.IsNotNull(messageResult.properties, "Message does not have basic properties.");
            AssertMessagePropertiesAreEqual(properties, messageResult.properties);
            CollectionAssert.AreEqual(messageContent, messageResult.body);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2);
        }
    }

    [TestMethod]
    public async Task RelayMessageTo2RabbitMQInstances()
    {
        await using var rabbit1 = new RabbitRunner(5673);
        await using var rabbit2 = new RabbitRunner(5674);
        await using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPorts: new[] { 5002, 5003 });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpoint: null);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);

        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            var messageId = Guid.NewGuid().ToString();
            var messageContent = Guid.NewGuid().ToByteArray();

            using var connection1 = rabbit1.CreateConnection();

            using var connection2 = rabbit2.CreateConnection();
            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            using var connection3 = rabbit3.CreateConnection();
            connection3.Model.ExchangeDeclare(bunny3.InboundExchange.Name, ExchangeType.Fanout);
            var queue3 = connection3.Consume(bunny3.InboundExchange.Name!);

            var properties1 = connection1.CreateProperties();
            properties1.MessageId = messageId;
            connection1.Publish(bunny1.OutboundExchange.Name!, properties1, messageContent);

            var messageResult = await WaitForMessage(queue2);

            Assert.IsNotNull(messageResult.properties, "Message does not have basic properties.");
            Assert.AreEqual(messageId, messageResult.properties.MessageId);
            CollectionAssert.AreEqual(messageContent, messageResult.body);

            messageResult = await WaitForMessage(queue3);

            Assert.IsNotNull(messageResult.properties, "Message does not have basic properties.");
            Assert.AreEqual(messageId, messageResult.properties.MessageId);
            CollectionAssert.AreEqual(messageContent, messageResult.body);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2, bunny3);
        }
    }

    [TestMethod]
    public async Task RelayMessagesFrom2RabbitMQInstances()
    {
        await using var rabbit1 = new RabbitRunner(5673);
        await using var rabbit2 = new RabbitRunner(5674);
        await using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5003);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5003);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);

        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();
            using var connection2 = rabbit2.CreateConnection();

            var messageList1 = new List<RabbitMessage>();
            for (int i = 0; i < 1000; i++)
            {
                var properties = connection1.CreateProperties();
                properties.MessageId = "MA-" + i.ToString("00000", CultureInfo.InvariantCulture);
                messageList1.Add((properties, Guid.NewGuid().ToByteArray()));
            }

            var messageList2 = new List<RabbitMessage>();
            for (int i = 0; i < 1000; i++)
            {
                var properties = connection2.CreateProperties();
                properties.MessageId = "MB-" + i.ToString("00000", CultureInfo.InvariantCulture);
                messageList2.Add((properties, Guid.NewGuid().ToByteArray()));
            }

            using var connection3 = rabbit3.CreateConnection();
            connection3.Model.ExchangeDeclare(bunny3.InboundExchange.Name, ExchangeType.Fanout);
            var queue3 = connection3.Consume(bunny3.InboundExchange.Name!);

            var task1 = Task.Run(() =>
            {
                foreach (var item in messageList1)
                {
                    connection1.Publish(bunny1.OutboundExchange.Name!, item.properties, item.body);
                }
            });
            var task2 = Task.Run(() =>
            {
                foreach (var item in messageList2)
                {
                    connection2.Publish(bunny2.OutboundExchange.Name!, item.properties, item.body);
                }
            });
            await Task.WhenAll(task1, task2);

            var messageResults = new Queue<RabbitMessage>();
            for (int i = 0; i < 2000; i++)
            {
                var messageResult = await WaitForMessage(queue3);
                messageResults.Enqueue(messageResult);
            }

            var messageDictionary = messageList1.Concat(messageList2).ToDictionary(i => i.properties!.MessageId);
            foreach (var messageResult in messageResults)
            {
                Assert.IsNotNull(messageResult.properties, "Message does not have basic properties.");
                var messageId = messageResult.properties.MessageId;
                Assert.IsNotNull(messageId, "MessageId is null.");

                var message = messageDictionary[messageId];
                CollectionAssert.AreEqual(message.body, messageResult.body);
                messageDictionary.Remove(messageId);
            }

            Assert.AreEqual(0, messageDictionary.Count, "Message List count");

            var messageIds = messageResults.Select(m => m.properties!.MessageId)
                .Where(id => id.StartsWith("MA-", StringComparison.Ordinal)).ToList();
            for (int i = 1; i < messageIds.Count; i++)
            {
                Assert.IsTrue(string.CompareOrdinal(messageIds[i - 1], messageIds[i]) < 0, "Unexpected order '{0}' > '{1}'.", messageIds[i - 1], messageIds[i]);
            }

            messageIds = messageResults.Select(m => m.properties!.MessageId)
                .Where(id => id.StartsWith("MB-", StringComparison.Ordinal)).ToList();
            for (int i = 1; i < messageIds.Count; i++)
            {
                Assert.IsTrue(string.CompareOrdinal(messageIds[i - 1], messageIds[i]) < 0, "Unexpected order '{0}' > '{1}'.", messageIds[i - 1], messageIds[i]);
            }
        }
        finally
        {
            await KillBunnies(bunny1, bunny2, bunny3);
        }
    }

    private static async Task KillBunnies(params BunnyRunner[] bunnies)
    {
        for (int i = 0; i < bunnies.Length; i++)
        {
            var bunny = bunnies[i];
            await bunny.Stop();

            Trace.WriteLine(OutputSeparator);
            Trace.WriteLine($"Bunny {i + 1}");
            Trace.WriteLine(bunny.GetOutput());
        }

        for (int i = 0; i < bunnies.Length; i++)
        {
            Assert.AreEqual(0, bunnies[i].ExitCode, $"Exit code - Bunny {i + 1}");
        }
    }

    private static async Task<RabbitMessage> WaitForMessage(
        IProducerConsumerCollection<RabbitMessage> queue,
        TimeSpan? timeout = default)
    {
        var expiration = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow <= expiration)
        {
            if (queue.TryTake(out var message))
            {
                return message;
            }

            await Task.Delay(100);
        }

        if (queue.TryTake(out var item))
        {
            return item;
        }

        throw new TimeoutException("Timeout waiting for message from queue.");
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
