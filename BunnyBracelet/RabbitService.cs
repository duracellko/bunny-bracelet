using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BunnyBracelet;

public sealed class RabbitService : IDisposable
{
    private readonly IOptions<RabbitOptions> options;

    private readonly Lazy<IConnection> connection;
    private readonly Lazy<IModel> sendChannel;
    private bool disposed;
    private volatile bool disposing;

    public RabbitService(IOptions<RabbitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        connection = new Lazy<IConnection>(CreateConnection);
        sendChannel = new Lazy<IModel>(CreateSendChannel);
    }

    public void SendMessage(Message message)
    {
        CheckDisposed();

        // Ensure that connection is opened;
        _ = connection.Value;
        sendChannel.Value.BasicPublish(options.Value.InboundExchange, string.Empty, message.Properties, message.Body);
    }

    public IBasicProperties CreateBasicProperties()
    {
        CheckDisposed();

        // Ensure that connection is opened;
        _ = connection.Value;
        return sendChannel.Value.CreateBasicProperties();
    }

    public IDisposable ConsumeMessages(Func<Message, Task> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        CheckDisposed();

        var exchangeName = options.Value.OutboundExchange;
        if (string.IsNullOrEmpty(exchangeName))
        {
            throw new InvalidOperationException("Outbound Exchange must be specified.");
        }

        var channel = connection.Value.CreateModel();
        try
        {
            channel.ExchangeDeclare(exchangeName, ExchangeType.Fanout);
            var messageConsumer = new MessageConsumer(channel, exchangeName, process);
            messageConsumer.Initialize();
            return messageConsumer;
        }
        catch (Exception)
        {
            channel.Close();
            channel.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposing = true;

            if (connection.IsValueCreated)
            {
                connection.Value.Close();
                connection.Value.Dispose();
            }

            disposed = true;
        }
    }

    private IConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var connectionFactory = new ConnectionFactory
        {
            Uri = options.Value.RabbitMQUri,
            DispatchConsumersAsync = true
        };
        return connectionFactory.CreateConnection();
    }

    private IModel CreateSendChannel()
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var channel = connection.Value.CreateModel();
        channel.ExchangeDeclare(options.Value.InboundExchange, ExchangeType.Fanout);
        return channel;
    }

    private void CheckDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class MessageConsumer : IDisposable
    {
        private readonly IModel channel;
        private readonly string exchangeName;
        private readonly Func<Message, Task> process;

        private bool disposed;
        private string? consumerTag;

        public MessageConsumer(IModel channel, string exchangeName, Func<Message, Task> process)
        {
            this.channel = channel;
            this.exchangeName = exchangeName;
            this.process = process;
        }

        public void Initialize()
        {
            var queue = channel.QueueDeclare();
            var queueName = queue.QueueName;
            channel.QueueBind(queueName, exchangeName, string.Empty);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += ConsumerOnReceived;
            consumerTag = channel.BasicConsume(queueName, false, consumer);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (consumerTag != null)
                {
                    channel.BasicCancel(consumerTag);
                }

                channel.Close();
                channel.Dispose();
                disposed = true;
            }
        }

        private async Task ConsumerOnReceived(object? sender, BasicDeliverEventArgs e)
        {
            try
            {
                var message = new Message(e.Body, e.BasicProperties);
                await process(message);
                channel.BasicAck(e.DeliveryTag, false);
            }
            catch (Exception)
            {
                channel.BasicNack(e.DeliveryTag, false, true);
                throw;
            }
        }
    }
}
