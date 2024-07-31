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

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        RabbitRunner.Reset();
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        RabbitRunner.Reset();
        using var rabbit = new RabbitRunner();
        await rabbit.Cleanup();
    }

    [TestMethod]
    public async Task SendMessageBetween2RabbitMQInstances()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);

        await rabbit1.Cleanup();
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
            MessageAssert.ArePropertiesEqual(properties, messageResult.properties);
            MessageAssert.AreBodiesEqual(messageContent, messageResult.body);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2);
        }
    }

    [TestMethod]
    public async Task RelayMessageTo2RabbitMQInstances()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPorts: new[] { 5002, 5003 });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpoint: null);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);

        await rabbit1.Cleanup();
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
            MessageAssert.AreBodiesEqual(messageContent, messageResult.body);

            messageResult = await WaitForMessage(queue3);

            Assert.IsNotNull(messageResult.properties, "Message does not have basic properties.");
            Assert.AreEqual(messageId, messageResult.properties.MessageId);
            MessageAssert.AreBodiesEqual(messageContent, messageResult.body);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2, bunny3);
        }
    }

    [TestMethod]
    public async Task RelayMessagesFrom2RabbitMQInstances()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5003);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5003);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();
            using var connection2 = rabbit2.CreateConnection();

            var messageList1 = CreateMessages(connection1, "MA-");
            var messageList2 = CreateMessages(connection2, "MB-");

            using var connection3 = rabbit3.CreateConnection();
            connection3.Model.ExchangeDeclare(bunny3.InboundExchange.Name, ExchangeType.Fanout);
            var queue3 = connection3.Consume(bunny3.InboundExchange.Name!);

            var task1 = SendMessages(connection1, messageList1, bunny1.OutboundExchange.Name!);
            var task2 = SendMessages(connection2, messageList2, bunny2.OutboundExchange.Name!);
            await Task.WhenAll(task1, task2);

            var messageResults = await WaitForMessages(queue3, 2000);

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
            AssertCollectionIsOrdered(messageIds);

            messageIds = messageResults.Select(m => m.properties!.MessageId)
                .Where(id => id.StartsWith("MB-", StringComparison.Ordinal)).ToList();
            AssertCollectionIsOrdered(messageIds);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2, bunny3);
        }

        static List<RabbitMessage> CreateMessages(RabbitConnection connection, string prefix)
        {
            var result = new List<RabbitMessage>(1000);
            for (int i = 0; i < 1000; i++)
            {
                var properties = connection.CreateProperties();
                properties.MessageId = prefix + i.ToString("00000", CultureInfo.InvariantCulture);
                result.Add((properties, Guid.NewGuid().ToByteArray()));
            }

            return result;
        }

        static async Task SendMessages(RabbitConnection connection, List<RabbitMessage> messages, string exchange)
        {
            await Task.Run(() =>
            {
                foreach (var item in messages)
                {
                    connection.Publish(exchange, item.properties, item.body);
                }
            });
        }
    }

    [TestMethod]
    public async Task NetworkDisconnectedOnSecondMessage()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);
        var queueName = "bunny-test-" + Guid.NewGuid().ToString();

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();

            var messages = new List<RabbitMessage>();
            for (int i = 0; i < 3; i++)
            {
                var properties = connection1.CreateProperties();
                properties.MessageId = i.ToString(CultureInfo.InvariantCulture);
                messages.Add((properties, Guid.NewGuid().ToByteArray()));
            }

            using (var connection2 = rabbit2.CreateConnection())
            {
                connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[0].properties, messages[0].body);

                var messageResult = await WaitForMessage(queue2);
                Assert.IsNotNull(messageResult.properties, "Message 1 does not have basic properties.");
                MessageAssert.ArePropertiesEqual(messages[0].properties!, messageResult.properties);
                MessageAssert.AreBodiesEqual(messages[0].body, messageResult.body);
            }

            await rabbit2.DisconnectContainerFromNetwork();
            await Task.Delay(200);
            connection1.Publish(bunny1.OutboundExchange.Name!, messages[1].properties, messages[1].body);
            await Task.Delay(200);
            await rabbit2.ConnectContainerToNetwork();

            using (var connection2 = rabbit2.CreateConnection())
            {
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[2].properties, messages[2].body);

                var messageResult = await WaitForMessage(queue2);
                Assert.IsNotNull(messageResult.properties, "Message 2 does not have basic properties.");
                MessageAssert.ArePropertiesEqual(messages[1].properties!, messageResult.properties);
                MessageAssert.AreBodiesEqual(messages[1].body, messageResult.body);

                messageResult = await WaitForMessage(queue2);
                Assert.IsNotNull(messageResult.properties, "Message 2 does not have basic properties.");
                MessageAssert.ArePropertiesEqual(messages[2].properties!, messageResult.properties);
                MessageAssert.AreBodiesEqual(messages[2].body, messageResult.body);
            }
        }
        finally
        {
            await KillBunnies(bunny1, bunny2);
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
    public async Task TooLargeMessage()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();
            using var connection2 = rabbit2.CreateConnection();

            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            var properties = connection1.CreateProperties();
            properties.MessageId = Guid.NewGuid().ToString();

            // Maximum HTTP request size in ASP.NET Core
            var body = new byte[30000000];
            var random = new Random();
            random.NextBytes(body);

            connection1.Publish(bunny1.OutboundExchange.Name!, properties, body);

            const string expectedOutput = "Message rejected by consumer";
            var timeout = DateTime.UtcNow.AddSeconds(5);
            var foundOutput = false;
            while (DateTime.UtcNow < timeout && !foundOutput)
            {
                await Task.Delay(1000);
                foundOutput = bunny1.GetOutput().Contains(expectedOutput, StringComparison.OrdinalIgnoreCase);
            }

            Assert.IsTrue(
                bunny1.GetOutput().Contains(expectedOutput, StringComparison.OrdinalIgnoreCase),
                "Missing bunny 1 log: Message rejected by consumer");
            Assert.AreEqual(0, queue2.Count);
        }
        finally
        {
            await KillBunnies(bunny1, bunny2);
        }
    }

    [TestMethod]
    public async Task CloseBunnyBraceletWithDisconnectedNetwork()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            using var connection1 = rabbit1.CreateConnection();

            var properties = connection1.CreateProperties();
            properties.MessageId = Guid.NewGuid().ToString();
            var body = Guid.NewGuid().ToByteArray();
            connection1.Publish(bunny1.OutboundExchange.Name!, properties, body);

            await Task.Delay(200);
            await rabbit2.DisconnectContainerFromNetwork();
            await Task.Delay(200);

            await bunny2.Stop();
            Assert.AreEqual(0, bunny2.ExitCode, $"Exit code - Bunny 2");

            await bunny1.Stop();
            Assert.AreEqual(0, bunny1.ExitCode, $"Exit code - Bunny 1");
        }
        finally
        {
            await rabbit2.ConnectContainerToNetwork();
            await KillBunnies(bunny1, bunny2);
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

    private static async Task<List<RabbitMessage>> WaitForMessages(
        IProducerConsumerCollection<RabbitMessage> queue,
        int count)
    {
        var result = new List<RabbitMessage>();
        for (int i = 0; i < 2000; i++)
        {
            var messageResult = await WaitForMessage(queue);
            result.Add(messageResult);
        }

        return result;
    }

    private static void AssertCollectionIsOrdered<T>(IReadOnlyList<T> collection)
        where T : IComparable<T>
    {
        if (collection.Count > 1)
        {
            Assert.IsNotNull(collection[0]);

            for (int i = 1; i < collection.Count; i++)
            {
                Assert.IsNotNull(collection[i]);
                Assert.IsTrue(collection[i - 1].CompareTo(collection[i]) < 0, "Unexpected order '{0}' > '{1}'.", collection[i - 1], collection[i]);
            }
        }
    }
}
