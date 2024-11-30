using System.Collections.Concurrent;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

using RabbitMessage = (RabbitMQ.Client.IReadOnlyBasicProperties? properties, byte[] body);

namespace BunnyBracelet.SystemTests;

/// <summary>
/// This object manages connection to RabbitMQ and provides simple operations
/// to publish and consume messages.
/// </summary>
internal sealed class RabbitConnection : IAsyncDisposable
{
    private readonly List<MessageConsumer> messageConsumers = [];
    private IConnection? connection;
    private IChannel? channel;

    public RabbitConnection(string uri)
    {
        Uri = new Uri(uri);
    }

    public Uri Uri { get; }

    public static BasicProperties CreateProperties()
    {
        return new BasicProperties
        {
            DeliveryMode = DeliveryModes.Transient
        };
    }

    public async ValueTask<IConnection> GetConnection()
    {
        if (connection is null)
        {
            var connectionFactory = new ConnectionFactory
            {
                Uri = Uri
            };
            connection = await connectionFactory.CreateConnectionAsync();
        }

        return connection;
    }

    public async ValueTask<IChannel> GetChannel()
    {
        if (channel is null)
        {
            var connection = await GetConnection();
            channel = await connection.CreateChannelAsync();
        }

        return channel;
    }

    public async Task Publish(string exchange, IReadOnlyBasicProperties? properties, ReadOnlyMemory<byte> body)
    {
        var channel = await GetChannel();

        if (properties is not null)
        {
            var basicProperties = new BasicProperties(properties);
            await channel.BasicPublishAsync(exchange, string.Empty, false, basicProperties, body);
        }
        else
        {
            await channel.BasicPublishAsync(exchange, string.Empty, false, body);
        }
    }

    public async Task<IProducerConsumerCollection<RabbitMessage>> Consume(string exchange, string? queue = null)
    {
        var connection = await GetConnection();
        var channel = await connection.CreateChannelAsync();
        var messageConsumer = new MessageConsumer(channel, exchange, queue);
        await messageConsumer.Initialize();
        messageConsumers.Add(messageConsumer);
        return messageConsumer.Queue;
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }

        if (channel is not null)
        {
            await channel.DisposeAsync();
        }
    }

    private sealed class MessageConsumer
    {
        private readonly IChannel channel;
        private readonly string exchange;
        private readonly string? queueName;
        private readonly ConcurrentQueue<RabbitMessage> queue = new();

        public MessageConsumer(IChannel channel, string exchange, string? queueName)
        {
            this.channel = channel;
            this.exchange = exchange;
            this.queueName = queueName;
        }

        public IProducerConsumerCollection<RabbitMessage> Queue => queue;

        public async Task Initialize()
        {
            var queue = await channel.QueueDeclareAsync(
                queueName ?? string.Empty,
                !string.IsNullOrEmpty(queueName),
                exclusive: string.IsNullOrEmpty(queueName),
                autoDelete: string.IsNullOrEmpty(queueName));
            await channel.QueueBindAsync(queue.QueueName, exchange, string.Empty);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += ConsumerOnReceived;
            await channel.BasicConsumeAsync(queue.QueueName, true, consumer);
        }

        private Task ConsumerOnReceived(object? sender, BasicDeliverEventArgs e)
        {
            queue.Enqueue((e.BasicProperties, e.Body.ToArray()));
            return Task.CompletedTask;
        }
    }
}
