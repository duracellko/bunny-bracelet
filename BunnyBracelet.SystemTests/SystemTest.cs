using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

using ByteArray = byte[];
#pragma warning disable SA1008 // Opening parenthesis should be spaced correctly
using RabbitMessage = (RabbitMQ.Client.IBasicProperties? properties, byte[] body);
#pragma warning restore SA1008 // Opening parenthesis should be spaced correctly

namespace BunnyBracelet.SystemTests;

[TestClass]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Creating test data is not performance critical.")]
public class SystemTest
{
    private const string OutputSeparator = "----------";

    private static readonly Lazy<Random> Random = new Lazy<Random>(() => new Random());

    [ClassInitialize]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Required by MS Test framework.")]
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
    [DataRow(0)]
    [DataRow(1)]
    public async Task SendMessageFromOneRabbitMQInstanceToAnother(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange: string.Empty, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, outboundExchange: string.Empty, endpointPort: 5001);
        SetupAuthentication(keyIndex, bunny1, bunny2);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2);

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

            var messageContent = GetRandomBytes(2224);
            connection1.Publish(bunny1.OutboundExchange.Name!, properties, messageContent);

            await AssertMessageInQueue(queue2, (properties, messageContent), 1);
            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task RelayMessageTo2RabbitMQInstances(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPorts: new[] { 5002, 5003 });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpoint: null);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);
        SetupAuthentication(keyIndex, 32, bunny1, bunny2, bunny3);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2, bunny3);

            var messageId = Guid.NewGuid().ToString();
            var messageContent = GetRandomBytes(5000);

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

            await AssertMessageInQueue(queue2, (properties1, messageContent), 1);
            await AssertMessageInQueue(queue3, (properties1, messageContent), 1);
            await AssertHealthy(bunny1, bunny2, bunny3);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2, bunny3);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [SuppressMessage("Style", "IDE0042:Deconstruct variable declaration", Justification = "Prefer no deconstruction in foreach statement.")]
    public async Task RelayMessagesFrom2RabbitMQInstances(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5003);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5003);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);
        SetupAuthentication(keyIndex, 128, bunny1, bunny2, bunny3);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            await AssertHealthy(bunny1, bunny2);
            await AssertUnhealthy(bunny3);

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
                var messageId = GetMessageId(messageResult);
                var (_, body) = messageDictionary[messageId];
                CollectionAssert.AreEqual(body, messageResult.body);
                messageDictionary.Remove(messageId);
            }

            Assert.AreEqual(0, messageDictionary.Count, "All messages should be in queue 3.");

            // Verify that messages were received in correct order.
            var messageIds = messageResults.Select(m => m.properties!.MessageId)
                .Where(id => id.StartsWith("MA-", StringComparison.Ordinal)).ToList();
            AssertCollectionIsOrdered(messageIds);

            messageIds = messageResults.Select(m => m.properties!.MessageId)
                .Where(id => id.StartsWith("MB-", StringComparison.Ordinal)).ToList();
            AssertCollectionIsOrdered(messageIds);

            await AssertHealthy(bunny1, bunny2, bunny3);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2, bunny3);
        }

        static List<RabbitMessage> CreateMessages(RabbitConnection connection, string prefix)
        {
            var result = new List<RabbitMessage>(1000);
            for (var i = 0; i < 1000; i++)
            {
                var properties = connection.CreateProperties();
                properties.MessageId = prefix + i.ToString("00000", CultureInfo.InvariantCulture);
                result.Add((properties, GetRandomBytes(1551)));
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

        static string GetMessageId(RabbitMessage message)
        {
            Assert.IsNotNull(message.properties, "Message does not have basic properties.");
            var messageId = message.properties.MessageId;
            Assert.IsNotNull(messageId, "MessageId is null.");
            return messageId;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task SendMessageWithAllHeaderValueTypes(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange: string.Empty, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, outboundExchange: string.Empty, endpointPort: 5001);
        SetupAuthentication(keyIndex, 100, bunny1, bunny2);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2);

            using var connection1 = rabbit1.CreateConnection();

            using var connection2 = rabbit2.CreateConnection();
            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            var properties = connection1.CreateProperties();
            properties.Headers = CreateHeaders();

            var messageContent = GetRandomBytes(5000000);
            connection1.Publish(bunny1.OutboundExchange.Name!, properties, messageContent);

            await AssertMessageInQueue(queue2, (properties, messageContent), 1);
            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }

        static Dictionary<string, object> CreateHeaders()
        {
            var headers = new Dictionary<string, object>
            {
                { "test binary", Encoding.UTF8.GetBytes("My test binary value") },
                { "test boolean", true },
                { "test byte", (byte)22 },
                { "test int16", short.MinValue },
                { "test int32", int.MinValue },
                { "test int64", long.MinValue },
                { "test uint16", ushort.MaxValue },
                { "test uint32", uint.MaxValue },
                { "test single", 123.456f },
                { "test double", 987654e-56 },
                { "test decimal", 12345.6789m },
                { "test timestamp", new AmqpTimestamp(DateTimeOffset.Now.ToUnixTimeSeconds()) }
            };

            var innerList = new List<object>
            {
                (byte)255,
                -1234567890123456789,
                GetRandomBytes(5000),
                false,
                true,
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
                2025u,
                (ushort)2026,
                2027L,
                2028,
                (short)2029,
                (byte)0,
                false,
                innerList,
                GetRandomBytes(120)
            };

            headers.Add("test list", list);

            return headers;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task NetworkDisconnectedOnSecondMessage(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);
        SetupAuthentication(keyIndex, bunny1, bunny2);
        var queueName = "bunny-test-" + Guid.NewGuid().ToString();

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1, bunny2);
            using var connection1 = rabbit1.CreateConnection();

            var messages = new List<RabbitMessage>();
            for (var i = 0; i < 3; i++)
            {
                var properties = connection1.CreateProperties();
                properties.MessageId = i.ToString(CultureInfo.InvariantCulture);
                messages.Add((properties, GetRandomBytes(4020)));
            }

            using (var connection2 = rabbit2.CreateConnection())
            {
                connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[0].properties, messages[0].body);

                await AssertMessageInQueue(queue2, messages[0], 1);
            }

            await rabbit2.DisconnectContainerFromNetwork();
            await Task.Delay(200);
            connection1.Publish(bunny1.OutboundExchange.Name!, messages[1].properties, messages[1].body);
            await Task.Delay(200);

            await AssertUnhealthy(bunny2);
            await AssertHealthy(bunny1);
            await rabbit2.ConnectContainerToNetwork();

            using (var connection2 = rabbit2.CreateConnection())
            {
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[2].properties, messages[2].body);

                await AssertMessageInQueue(queue2, messages[1], 2);
                await AssertMessageInQueue(queue2, messages[2], 3);
            }

            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    [TestMethod]
    public async Task TooLargeMessage()
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);

        var endpoint = new EndpointSettings
        {
            Uri = BunnyRunner.GetUri(5002),
            QueueName = "large-msg-queue-" + Guid.NewGuid().ToString(),
            AutoDelete = false
        };
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpoints: new[] { endpoint });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpointPort: 5001);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1, bunny2);

            using var connection1 = rabbit1.CreateConnection();
            using var connection2 = rabbit2.CreateConnection();

            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            var properties = connection1.CreateProperties();
            properties.MessageId = Guid.NewGuid().ToString();

            // Maximum HTTP request size in ASP.NET Core
            var body = GetRandomBytes(30000000);
            connection1.Publish(bunny1.OutboundExchange.Name!, properties, body);

            await WaitForLogOutput(bunny1, "Message rejected by consumer");

            var queueMessageCount = (int)connection1.Model.MessageCount(endpoint.QueueName);
            Assert.AreEqual(0, queueMessageCount, "Outbound queue should be empty.");
            Assert.AreEqual(0, queue2.Count, "Inbound queue should be empty.");

            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task RestartRabbitMQInstancesWithDurableQueues(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);

        var inboundExchange1 = CreateExchangeSettings("durable-inbound");
        var outboundExchange1 = CreateExchangeSettings("durable-outbound");
        var endpoint1 = CreateEndpointSettings(5002);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange1, outboundExchange1, endpoints: new[] { endpoint1 });

        var inboundExchange2 = CreateExchangeSettings("durable-inbound");
        var outboundExchange2 = CreateExchangeSettings("durable-outbound");
        var endpoint2 = CreateEndpointSettings(5001);
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, inboundExchange2, outboundExchange2, endpoints: new[] { endpoint2 });

        SetupAuthentication(keyIndex, 79, bunny1, bunny2);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            var queueName = "bunny-test-" + Guid.NewGuid().ToString();
            var messages = new List<RabbitMessage>();
            await AssertHealthy(bunny1, bunny2);

            using (var connection1 = rabbit1.CreateConnection())
            {
                for (var i = 0; i < 3; i++)
                {
                    var properties = connection1.CreateProperties();
                    properties.MessageId = i.ToString(CultureInfo.InvariantCulture);
                    properties.Persistent = true;
                    messages.Add((properties, GetRandomBytes(4000)));
                }

                using (var connection2 = rabbit2.CreateConnection())
                {
                    connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout, true);
                    var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                    connection1.Publish(bunny1.OutboundExchange.Name!, messages[0].properties, messages[0].body);

                    await AssertMessageInQueue(queue2, messages[0], 1);
                }

                await rabbit2.DisconnectContainerFromNetwork();
                await Task.Delay(200);
                await rabbit2.StopContainer();

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[1].properties, messages[1].body);

                await AssertHealthy(bunny1);
                await AssertUnhealthy(bunny2);
            }

            await Task.Delay(200);
            await rabbit1.DisconnectContainerFromNetwork();
            await Task.Delay(200);
            await rabbit1.StopContainer();

            await AssertUnhealthy(bunny1, bunny2);

            await Task.Delay(500);
            await rabbit1.ConnectContainerToNetwork();
            await rabbit1.StartContainer();

            using (var connection1 = rabbit1.CreateConnection())
            {
                connection1.Publish(bunny1.OutboundExchange.Name!, messages[2].properties, messages[2].body);

                await rabbit2.ConnectContainerToNetwork();
                await rabbit2.StartContainer();

                using var connection2 = rabbit2.CreateConnection();
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);
                await AssertMessageInQueue(queue2, messages[1], 2);
                await AssertMessageInQueue(queue2, messages[2], 3);
            }

            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }

        static ExchangeSettings CreateExchangeSettings(string prefix)
        {
            return new ExchangeSettings
            {
                Name = $"{prefix}-{Guid.NewGuid()}",
                Durable = true
            };
        }

        static EndpointSettings CreateEndpointSettings(int port)
        {
            return new EndpointSettings
            {
                Uri = BunnyRunner.GetUri(port),
                QueueName = $"test-relay-{Guid.NewGuid()}",
                Durable = true,
                AutoDelete = false
            };
        }
    }

    [TestMethod]
    public async Task StopDeliveryRetryAfterReachingMessageExpiration()
    {
        const string expiration = "4200";
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);

        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange: string.Empty, endpointPort: 5002);
        bunny1.RequeueDelay = 1000;
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, outboundExchange: string.Empty, endpointPort: default);
        var queueName = "bunny-test-" + Guid.NewGuid().ToString();

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2);

            using var connection1 = rabbit1.CreateConnection();

            var messages = new List<RabbitMessage>();
            for (var i = 0; i < 3; i++)
            {
                var properties = connection1.CreateProperties();
                properties.MessageId = i.ToString(CultureInfo.InvariantCulture);
                properties.Expiration = expiration;
                messages.Add((properties, GetRandomBytes(5000)));
            }

            using (var connection2 = rabbit2.CreateConnection())
            {
                connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[0].properties, messages[0].body);

                await AssertMessageInQueue(queue2, messages[0], 1);
            }

            await rabbit2.DisconnectContainerFromNetwork();
            await Task.Delay(200);

            // Scenario:
            // T+0: queue message 1
            // T+1000: 1st retry to relay message 1
            // T+2000: 2nd retry to relay message 1
            // T+3000: 3rd retry to relay message 1
            // T+3500: queue message 2
            // T+4000: 4th retry to relay message 1
            // T+4100: restore network connestion of rabbit 2
            // T+4200: message 1 expires
            // T+5000: end pause after 4th retry relay message 1
            // T+5000: relay message 2 - fails, because connection is not recovered yet
            // T+5000: network connection recovery of bunny 2
            // T+6000: 1st retry to relay message 2
            //
            // Note: Network recovery interval is 5 seconds
            connection1.Publish(bunny1.OutboundExchange.Name!, messages[1].properties, messages[1].body);
            await Task.WhenAll(
                Task.Delay(3500),
                Task.Run(async () => await AssertHealthy(bunny1)),
                Task.Run(async () => await AssertUnhealthy(bunny2)));

            connection1.Publish(bunny1.OutboundExchange.Name!, messages[2].properties, messages[2].body);
            await Task.Delay(600);
            await rabbit2.ConnectContainerToNetwork();

            using (var connection2 = rabbit2.CreateConnection())
            {
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);
                await AssertMessageInQueue(queue2, messages[2], 3);
            }

            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task MessageExpirationIsCopiedToDestination(int keyIndex)
    {
        const string expiration = "4200";
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);

        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange: string.Empty, endpointPort: 5002);
        bunny1.RequeueDelay = 1000;
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, outboundExchange: string.Empty, endpointPort: default);
        SetupAuthentication(keyIndex, bunny1, bunny2);
        var queueName = "bunny-test-" + Guid.NewGuid().ToString();

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2);

            using var connection1 = rabbit1.CreateConnection();

            var messages = new List<RabbitMessage>();
            for (var i = 0; i < 3; i++)
            {
                var properties = connection1.CreateProperties();
                properties.MessageId = i.ToString(CultureInfo.InvariantCulture);
                properties.Expiration = expiration;
                messages.Add((properties, GetRandomBytes(842)));
            }

            using (var connection2 = rabbit2.CreateConnection())
            {
                connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);

                connection1.Publish(bunny1.OutboundExchange.Name!, messages[0].properties, messages[0].body);

                await AssertMessageInQueue(queue2, messages[0], 1);
            }

            await rabbit2.DisconnectContainerFromNetwork();
            await Task.Delay(200);

            // Same scenario as StopDeliveryRetryAfterReachingMessageTTL
            connection1.Publish(bunny1.OutboundExchange.Name!, messages[1].properties, messages[1].body);
            await Task.WhenAll(
                Task.Delay(3500),
                Task.Run(async () => await AssertHealthy(bunny1)),
                Task.Run(async () => await AssertUnhealthy(bunny2)));

            connection1.Publish(bunny1.OutboundExchange.Name!, messages[2].properties, messages[2].body);
            await Task.Delay(600);
            await rabbit2.ConnectContainerToNetwork();

            using (var connection2 = rabbit2.CreateConnection())
            {
                // Start consuming messages at T+8000
                // Notice that is after message 2 original expiration (T+7700)
                // However, message 2 expiration is reset, when it is queue by bunny 2.
                await Task.Delay(3900);
                var queue2 = connection2.Consume(bunny2.InboundExchange.Name!, queueName);
                await AssertMessageInQueue(queue2, messages[2], 3);
            }

            await AssertHealthy(bunny1, bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    [TestMethod]
    public async Task RelayOfMessageShouldTimeout()
    {
        using var rabbit1 = new RabbitRunner(5673);

        await using var fakeBunny2 = new FakeBunnyBracelet();
        await fakeBunny2.Start();

        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, endpoint: fakeBunny2.Uri!.ToString());
        bunny.Timeout = 1000;

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();
        var exchangeName = bunny.OutboundExchange.Name!;

        try
        {
            await AssertHealthy(bunny);

            var messageId = Guid.NewGuid().ToString();
            var messageContent = GetRandomBytes(100);

            using var connection1 = rabbit1.CreateConnection();
            var properties1 = connection1.CreateProperties();
            properties1.MessageId = messageId;
            connection1.Publish(exchangeName, properties1, messageContent);

            var expectedOutput = $"Relaying message (MessageId: {messageId}, CorrelationId: (null), Size: 100) from RabbitMQ exchange '{exchangeName}' to '{fakeBunny2.Uri}' failed.";
            await WaitForLogOutput(bunny, expectedOutput, TimeSpan.FromMilliseconds(1100));

            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    public async Task ShouldReturn403ForbiddenWhenInboundExchangeIsNotConfigured()
    {
        using var rabbit1 = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, inboundExchange: string.Empty, endpointPort: 5002);

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();

        try
        {
            await AssertHealthy(bunny);

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(bunny.Uri);

            var message = GetAuthenticatedMessage();
            using var content = new ByteArrayContent(message);
            var response = await httpClient.PostAsync(new Uri("message", UriKind.Relative), content);

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    public async Task ShouldReturn400BadRequestOnInvalidInput()
    {
        using var rabbit1 = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();

        try
        {
            await AssertHealthy(bunny);

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(bunny.Uri);

            var message = GetAuthenticatedMessage();
            message[12] = 12;
            using var content = new ByteArrayContent(message);
            var response = await httpClient.PostAsync(new Uri("message", UriKind.Relative), content);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    public async Task ShouldReturn401UnauthorizedOnExpiredTimestamp(string useKeyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);

        ByteArray? key = null;
        if (!string.IsNullOrEmpty(useKeyIndex))
        {
            key = RandomNumberGenerator.GetBytes(64);
            bunny.AuthenticationKey1 = Convert.ToBase64String(key);
        }

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();

        try
        {
            await AssertHealthy(bunny);

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(bunny.Uri);

            var message = GetAuthenticatedMessage(key: key, timestampDifference: TimeSpan.FromMinutes(-5));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("message", UriKind.Relative));
            using var content = new ByteArrayContent(message);
            request.Content = content;

            if (!string.IsNullOrEmpty(useKeyIndex))
            {
                request.Headers.Add("BunnyBracelet-AuthenticationKeyIndex", useKeyIndex);
            }

            var response = await httpClient.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            await WaitForLogOutput(bunny, "Message authentication failed. Timestamp ");
            Assert.IsTrue(Regex.IsMatch(bunny.GetOutput(), @"Message authentication failed\. Timestamp '[^\']+' is expired\."));
            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    public async Task ShouldReturn401UnauthorizedOnInvalidKeyIndexHeader(string useKeyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);

        var key = RandomNumberGenerator.GetBytes(64);
        bunny.UseAuthenticationKeyIndex = 2;
        bunny.AuthenticationKey2 = Convert.ToBase64String(key);

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();

        try
        {
            await AssertHealthy(bunny);

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(bunny.Uri);

            var message = GetAuthenticatedMessage(key);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("message", UriKind.Relative));
            using var content = new ByteArrayContent(message);
            request.Content = content;

            if (!string.IsNullOrEmpty(useKeyIndex))
            {
                request.Headers.Add("BunnyBracelet-AuthenticationKeyIndex", useKeyIndex);
            }

            var response = await httpClient.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            var expectedLog = string.IsNullOrEmpty(useKeyIndex) ?
                "Missing authentication key index in HTTP header 'BunnyBracelet-AuthenticationKeyIndex'." :
                "Missing authentication key 1.";
            await WaitForLogOutput(bunny, expectedLog);
            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ShouldReturn401UnauthorizedOnIncorrectAuthenticationCode(bool invalidMessage)
    {
        using var rabbit1 = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit1.Uri, endpointPort: 5002);

        var key = RandomNumberGenerator.GetBytes(64);
        bunny.UseAuthenticationKeyIndex = 1;
        bunny.AuthenticationKey1 = Convert.ToBase64String(key);

        await rabbit1.Cleanup();
        await rabbit1.Start();
        await bunny.Start();

        try
        {
            await AssertHealthy(bunny);

            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(bunny.Uri);

            var message = GetAuthenticatedMessage(key);
            message[^1] ^= 1;
            if (invalidMessage)
            {
                message[12] = 12;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("message", UriKind.Relative));
            using var content = new ByteArrayContent(message);
            request.Headers.Add("BunnyBracelet-AuthenticationKeyIndex", "1");
            request.Content = content;

            var response = await httpClient.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            await WaitForLogOutput(bunny, "Message authentication using key 1 failed.");
            await AssertHealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task Initialize3rdEndpointRelayWhen1stFailedAnd2ndHasMissingUri(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);

        var endpoint1 = CreateEndpointSettings(5002);
        var endpoint2 = CreateEndpointSettings(null);
        var endpoint3 = CreateEndpointSettings(5003);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpoints: new[] { endpoint1, endpoint2, endpoint3 });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpoint: null);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);
        SetupAuthentication(keyIndex, bunny1, bunny2, bunny3);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());

        using var connection1 = rabbit1.CreateConnection();
        connection1.Model.QueueDeclare(endpoint1.QueueName);

        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            await AssertHealthy(bunny1);
            await AssertUnhealthy(bunny2, bunny3);

            var expectedOutput = $"Setup of relay from RabbitMQ exchange '{bunny1.OutboundExchange.Name}' and queue '{endpoint1.QueueName}' to endpoint '{endpoint1.Uri}/' failed.";
            await WaitForLogOutput(bunny1, expectedOutput);

            var messageId = Guid.NewGuid().ToString();
            var messageContent = GetRandomBytes(2000);

            using var connection2 = rabbit2.CreateConnection();
            connection2.Model.ExchangeDeclare(bunny2.InboundExchange.Name, ExchangeType.Fanout);
            var queue2 = connection2.Consume(bunny2.InboundExchange.Name!);

            using var connection3 = rabbit3.CreateConnection();
            connection3.Model.ExchangeDeclare(bunny3.InboundExchange.Name, ExchangeType.Fanout);
            var queue3 = connection3.Consume(bunny3.InboundExchange.Name!);

            var properties1 = connection1.CreateProperties();
            properties1.MessageId = messageId;
            connection1.Publish(bunny1.OutboundExchange.Name!, properties1, messageContent);

            await AssertMessageInQueue(queue3, (properties1, messageContent), 1);
            Assert.AreEqual(0, queue2.Count);

            await AssertHealthy(bunny1, bunny3);
            await AssertUnhealthy(bunny2);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2, bunny3);
        }

        static EndpointSettings CreateEndpointSettings(int? port)
        {
            return new EndpointSettings
            {
                Uri = port.HasValue ? BunnyRunner.GetUri(port.Value) : string.Empty,
                QueueName = $"test-relay-{Guid.NewGuid()}",
                Durable = false,
                AutoDelete = false
            };
        }
    }

    [TestMethod]
    public async Task NoRelayWhenOutboundExchangeIsNotConfigured()
    {
        using var rabbit = new RabbitRunner(5673);
        await using var bunny = BunnyRunner.Create(5001, rabbit.Uri, outboundExchange: string.Empty, endpointPort: 5002);

        await rabbit.Cleanup();
        await rabbit.Start();
        await bunny.Start();

        try
        {
            await AssertUnhealthy(bunny);

            var expectedOutput = "Outbound exchange is not configured. Message relay service is disabled. Configure setting 'BunnyBracelet.OutboundExchange'.";
            await WaitForLogOutput(bunny, expectedOutput);

            await AssertUnhealthy(bunny);
        }
        finally
        {
            await PutBunniesDown(bunny);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task TwoEndpointsOnTheSameQueueRelayMessageOnlyToOne(int keyIndex)
    {
        using var rabbit1 = new RabbitRunner(5673);
        using var rabbit2 = new RabbitRunner(5674);
        using var rabbit3 = new RabbitRunner(5675);

        var queueName = $"test-relay-{Guid.NewGuid()}";
        var endpoint1 = CreateEndpointSettings(5002, queueName);
        var endpoint2 = CreateEndpointSettings(5003, queueName);
        await using var bunny1 = BunnyRunner.Create(5001, rabbit1.Uri, endpoints: new[] { endpoint1, endpoint2 });
        await using var bunny2 = BunnyRunner.Create(5002, rabbit2.Uri, endpoint: null);
        await using var bunny3 = BunnyRunner.Create(5003, rabbit3.Uri, endpoint: null);
        SetupAuthentication(keyIndex, 800, bunny1, bunny2, bunny3);

        await rabbit1.Cleanup();
        await Task.WhenAll(rabbit1.Start(), rabbit2.Start(), rabbit3.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start(), bunny3.Start());

        try
        {
            var messageId = Guid.NewGuid().ToString();
            var messageContent = GetRandomBytes(5000);

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

            var (propertiesResult, bodyResult) = await WaitForMessage(queue2, queue3);
            Assert.IsNotNull(propertiesResult, $"Message does not have basic properties.");
            MessageAssert.ArePropertiesEqual(properties1, propertiesResult);
            MessageAssert.AreBodiesEqual(messageContent, bodyResult);

            await Task.Delay(100);
            Assert.AreEqual(0, queue2.Count);
            Assert.AreEqual(0, queue3.Count);
        }
        finally
        {
            await PutBunniesDown(bunny1, bunny2, bunny3);
        }

        static EndpointSettings CreateEndpointSettings(int port, string queue)
        {
            return new EndpointSettings
            {
                Uri = BunnyRunner.GetUri(port),
                QueueName = queue,
                Durable = false,
                AutoDelete = false
            };
        }

        static async Task<RabbitMessage> WaitForMessage(params IProducerConsumerCollection<RabbitMessage>[] queues)
        {
            var expiration = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow <= expiration)
            {
                foreach (var queue in queues)
                {
                    if (queue.TryTake(out var message))
                    {
                        return message;
                    }
                }

                await Task.Delay(100);
            }

            foreach (var queue in queues)
            {
                if (queue.TryTake(out var item))
                {
                    return item;
                }
            }

            throw new TimeoutException("Timeout waiting for message from queue.");
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
            var body = GetRandomBytes(4000);
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
            await PutBunniesDown(bunny1, bunny2);
        }
    }

    // Disclaimer: No bunnies get harmed by running this method or any of these tests.
    private static async Task PutBunniesDown(params BunnyRunner[] bunnies)
    {
        for (var i = 0; i < bunnies.Length; i++)
        {
            var bunny = bunnies[i];
            await bunny.Stop();

            Trace.WriteLine(OutputSeparator);
            Trace.WriteLine($"Bunny {i + 1}");
            Trace.WriteLine(bunny.GetOutput());
        }

        for (var i = 0; i < bunnies.Length; i++)
        {
            Assert.AreEqual(0, bunnies[i].ExitCode, $"Exit code - Bunny {i + 1}");
        }
    }

    private static void SetupAuthentication(int useKeyIndex, params BunnyRunner[] bunnies)
    {
        SetupAuthentication(useKeyIndex, 64, bunnies);
    }

    private static void SetupAuthentication(int useKeyIndex, int keySize, params BunnyRunner[] bunnies)
    {
        if (useKeyIndex > 0)
        {
            var key = RandomNumberGenerator.GetBytes(keySize);

            foreach (var bunny in bunnies)
            {
                bunny.UseAuthenticationKeyIndex = useKeyIndex;
                if (useKeyIndex == 2)
                {
                    bunny.AuthenticationKey2 = Convert.ToBase64String(key);
                    bunny.AuthenticationKey1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(keySize));
                }
                else
                {
                    bunny.AuthenticationKey1 = Convert.ToBase64String(key);
                    bunny.AuthenticationKey2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(keySize));
                }
            }
        }
    }

    private static byte[] GetAuthenticatedMessage(ByteArray? key = null, TimeSpan? timestampDifference = default)
    {
        var message = new byte[] { 82, 77, 81, 82, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0 };

        var timestamp = DateTime.UtcNow;
        if (timestampDifference.HasValue)
        {
            timestamp = timestamp.Add(timestampDifference.Value);
            BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(4, 8), timestamp.ToBinary());
        }

        if (key is not null)
        {
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(message);
            message = message.Concat(hash).ToArray();
        }

        return message;
    }

    private static async Task<RabbitMessage> WaitForMessage(
        IProducerConsumerCollection<RabbitMessage> queue,
        TimeSpan? timeout = default)
    {
        var timeoutTime = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow <= timeoutTime)
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
        int count = 2000)
    {
        var result = new List<RabbitMessage>();
        for (var i = 0; i < count; i++)
        {
            var messageResult = await WaitForMessage(queue);
            result.Add(messageResult);
        }

        return result;
    }

    private static async Task AssertMessageInQueue(IProducerConsumerCollection<RabbitMessage> queue, RabbitMessage expectedMessage, int index)
    {
        var (properties, body) = await WaitForMessage(queue);
        Assert.IsNotNull(properties, $"Message {index} does not have basic properties.");
        MessageAssert.ArePropertiesEqual(expectedMessage.properties!, properties);
        MessageAssert.AreBodiesEqual(expectedMessage.body, body);
    }

    private static async Task WaitForLogOutput(BunnyRunner bunny, string expectedOutput, TimeSpan? timeout = default)
    {
        var timeoutTime = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        var foundOutput = false;
        while (DateTime.UtcNow < timeoutTime && !foundOutput)
        {
            await Task.Delay(250);
            foundOutput = bunny.GetOutput().Contains(expectedOutput, StringComparison.OrdinalIgnoreCase);
        }

        Assert.IsTrue(foundOutput, "Missing log output: " + expectedOutput);
    }

    private static async Task AssertHealthy(params BunnyRunner[] bunnies)
    {
        for (var i = 0; i < bunnies.Length; i++)
        {
            var bunny = bunnies[i];
            var healthStatus = await bunny.GetHealthStatus();
            Assert.AreEqual(HealthStatus.Healthy, healthStatus, $"Bunny {i + 1}");
        }
    }

    private static async Task AssertUnhealthy(params BunnyRunner[] bunnies)
    {
        for (var i = 0; i < bunnies.Length; i++)
        {
            var bunny = bunnies[i];
            var healthStatus = await bunny.GetHealthStatus();
            Assert.AreEqual(HealthStatus.Unhealthy, healthStatus, $"Bunny {i + 1}");
        }
    }

    private static void AssertCollectionIsOrdered<T>(List<T> collection)
        where T : IComparable<T>
    {
        if (collection.Count > 1)
        {
            Assert.IsNotNull(collection[0]);

            for (var i = 1; i < collection.Count; i++)
            {
                Assert.IsNotNull(collection[i]);
                Assert.IsTrue(collection[i - 1].CompareTo(collection[i]) < 0, "Unexpected order '{0}' > '{1}'.", collection[i - 1], collection[i]);
            }
        }
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random is sufficient for test data.")]
    private static byte[] GetRandomBytes(int length)
    {
        var result = new byte[length];
        Random.Value.NextBytes(result);
        return result;
    }
}
