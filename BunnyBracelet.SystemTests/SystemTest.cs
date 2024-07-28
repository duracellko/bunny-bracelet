using System.Diagnostics;
using RabbitMQ.Client;

namespace BunnyBracelet.SystemTests;

[TestClass]
public class SystemTest
{
    private const string InboundExchange = "test-inbound";
    private const string OutboundExchange = "test-outbound";
    private const string OutputSeparator = "----------\r\n";

    [TestMethod]
    public async Task SendMessageBetween2RabbitMQInstances()
    {
        await using var rabbit1 = new RabbitRunner(5673);
        await using var rabbit2 = new RabbitRunner(5674);
        await using var bunny1 = new BunnyRunner(5001, rabbit1.Uri, InboundExchange, OutboundExchange, "http://localhost:5002");
        await using var bunny2 = new BunnyRunner(5002, rabbit2.Uri, InboundExchange, OutboundExchange, "http://localhost:5001");

        await Task.WhenAll(rabbit1.Start(), rabbit2.Start());
        await Task.WhenAll(bunny1.Start(), bunny2.Start());

        try
        {
            var messageId = Guid.NewGuid().ToString();
            var messageContent = Guid.NewGuid().ToByteArray();

            var rabbitConnectionFactory1 = new ConnectionFactory
            {
                Uri = new Uri(rabbit1.Uri)
            };
            var rabbitConnection1 = rabbitConnectionFactory1.CreateConnection();
            var rabbitChannel1 = rabbitConnection1.CreateModel();
            rabbitChannel1.ExchangeDeclare(OutboundExchange, ExchangeType.Fanout);

            var rabbitConnectionFactory2 = new ConnectionFactory
            {
                Uri = new Uri(rabbit2.Uri)
            };
            var rabbitConnection2 = rabbitConnectionFactory2.CreateConnection();
            var rabbitChannel2 = rabbitConnection2.CreateModel();
            rabbitChannel2.ExchangeDeclare(InboundExchange, ExchangeType.Fanout);
            var queue = rabbitChannel2.QueueDeclare();
            rabbitChannel2.QueueBind(queue.QueueName, InboundExchange, string.Empty);

            var properties1 = rabbitChannel1.CreateBasicProperties();
            properties1.MessageId = messageId;
            rabbitChannel1.BasicPublish(OutboundExchange, string.Empty, properties1, messageContent);

            BasicGetResult? messageResult = null;
            for (var timeout = DateTime.UtcNow.AddSeconds(5); messageResult is null && DateTime.UtcNow <= timeout;)
            {
                await Task.Delay(100);
                messageResult = rabbitChannel2.BasicGet(queue.QueueName, true);
            }

            Assert.IsNotNull(messageResult, "No message in queue.");
            Assert.IsNotNull(messageResult.BasicProperties, "Message does not have basic properties.");
            Assert.AreEqual(messageId, messageResult.BasicProperties.MessageId);
            CollectionAssert.AreEqual(messageContent, messageResult.Body.ToArray());
        }
        finally
        {
            Trace.WriteLine("Bunny1");
            Trace.WriteLine(bunny1.GetOutput());
            Trace.WriteLine(OutputSeparator);
            Trace.WriteLine("Bunny2");
            Trace.WriteLine(bunny2.GetOutput());
        }
    }
}
