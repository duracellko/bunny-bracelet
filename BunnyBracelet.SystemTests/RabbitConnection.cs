using System.Collections.Concurrent;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

#pragma warning disable SA1008 // Opening parenthesis should be spaced correctly
using RabbitMessage = (RabbitMQ.Client.IBasicProperties? properties, byte[] body);
#pragma warning restore SA1008 // Opening parenthesis should be spaced correctly

namespace BunnyBracelet.SystemTests;

internal sealed class RabbitConnection : IDisposable
{
    private readonly Lazy<IConnection> connection;
    private readonly Lazy<IModel> model;
    private readonly List<MessageConsumer> messageConsumers = new List<MessageConsumer>();

    public RabbitConnection(string uri)
    {
        Uri = new Uri(uri);
        connection = new Lazy<IConnection>(CreateConnection);
        model = new Lazy<IModel>(() => connection.Value.CreateModel());
    }

    public Uri Uri { get; }

    public IConnection Connection => connection.Value;

    public IModel Model => model.Value;

    public IBasicProperties CreateProperties() => Model.CreateBasicProperties();

    public void Publish(string exchange, IBasicProperties? properties, ReadOnlyMemory<byte> body)
    {
        Model.BasicPublish(exchange, string.Empty, properties, body);
    }

    public IProducerConsumerCollection<RabbitMessage> Consume(string exchange)
    {
        var model = Connection.CreateModel();
        var messageConsumer = new MessageConsumer(model, exchange);
        messageConsumer.Initialize();
        messageConsumers.Add(messageConsumer);
        return messageConsumer.Queue;
    }

    public void Dispose()
    {
        if (connection.IsValueCreated)
        {
            connection.Value.Close();
            connection.Value.Dispose();
        }
    }

    private IConnection CreateConnection()
    {
        var connectionFactory = new ConnectionFactory
        {
            Uri = Uri
        };
        return connectionFactory.CreateConnection();
    }

    private sealed class MessageConsumer
    {
        private readonly IModel model;
        private readonly string exchange;
        private readonly ConcurrentQueue<RabbitMessage> queue = new ConcurrentQueue<RabbitMessage>();

        public MessageConsumer(IModel model, string exchange)
        {
            this.model = model;
            this.exchange = exchange;
        }

        public IProducerConsumerCollection<RabbitMessage> Queue => queue;

        public void Initialize()
        {
            var queue = model.QueueDeclare();
            model.QueueBind(queue.QueueName, exchange, string.Empty);

            var consumer = new EventingBasicConsumer(model);
            consumer.Received += ConsumerOnReceived;
            model.BasicConsume(queue.QueueName, true, consumer);
        }

        private void ConsumerOnReceived(object? sender, BasicDeliverEventArgs e)
        {
            queue.Enqueue((e.BasicProperties, e.Body.ToArray()));
        }
    }
}
