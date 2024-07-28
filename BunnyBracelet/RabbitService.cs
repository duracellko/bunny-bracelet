using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BunnyBracelet;

public sealed class RabbitService : IDisposable
{
    private readonly IOptions<RabbitOptions> options;
    private readonly ILogger<RabbitService> logger;

    private readonly Lazy<IConnection> connection;
    private readonly Lazy<IModel> sendChannel;
    private bool disposed;
    private volatile bool disposing;

    public RabbitService(IOptions<RabbitOptions> options, ILogger<RabbitService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;
        connection = new Lazy<IConnection>(CreateConnection);
        sendChannel = new Lazy<IModel>(CreateSendChannel);
    }

    public void SendMessage(Message message)
    {
        CheckDisposed();

        var exchange = options.Value.InboundExchange!;
        logger.PublishingMessage(exchange, message.Properties, message.Body.Length);

        try
        {
            // Ensure that connection is opened;
            _ = connection.Value;
            sendChannel.Value.BasicPublish(options.Value.InboundExchange, string.Empty, message.Properties, message.Body);
            logger.MessagePublished(exchange, message.Properties, message.Body.Length);
        }
        catch (Exception ex)
        {
            logger.ErrorPublishingMessage(ex, exchange, message.Properties, message.Body.Length);
            throw;
        }
    }

    public IBasicProperties CreateBasicProperties()
    {
        CheckDisposed();

        // Ensure that connection is opened;
        _ = connection.Value;
        return sendChannel.Value.CreateBasicProperties();
    }

    public IDisposable ConsumeMessages(Func<Message, Task<ProcessMessageResult>> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        CheckDisposed();

        var exchangeName = options.Value.OutboundExchange;
        if (string.IsNullOrEmpty(exchangeName))
        {
            throw new InvalidOperationException("Outbound Exchange must be specified.");
        }

        logger.InitializingConsumer(exchangeName);

        var channel = connection.Value.CreateModel();
        try
        {
            channel.ExchangeDeclare(exchangeName, ExchangeType.Fanout);
            logger.ExchangeInitialized(exchangeName, ExchangeType.Fanout);

            var messageConsumer = new MessageConsumer(channel, exchangeName, process, logger);
            messageConsumer.Initialize();
            return messageConsumer;
        }
        catch (Exception ex)
        {
            logger.ErrorInitializingConsumer(ex, exchangeName);
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

        var uri = options.Value.RabbitMQUri;
        if (uri is null)
        {
            throw new InvalidOperationException("RabbitMQ URI must be specified.");
        }

        var connectionFactory = new ConnectionFactory
        {
            Uri = uri,
            DispatchConsumersAsync = true
        };

        logger.ConnectingToRabbitMQ(uri);

        var connection = connectionFactory.CreateConnection();
        connection.CallbackException += ConnectionOnCallbackException;
        return connection;
    }

    private IModel CreateSendChannel()
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var exchangeName = options.Value.InboundExchange!;
        var channel = connection.Value.CreateModel();
        channel.ExchangeDeclare(exchangeName, ExchangeType.Fanout);
        logger.ExchangeInitialized(exchangeName, ExchangeType.Fanout);
        return channel;
    }

    private void ConnectionOnCallbackException(object? sender, CallbackExceptionEventArgs e)
    {
        logger.ErrorConnectionCallback(e.Exception);
    }

    private void CheckDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class MessageConsumer : IDisposable
    {
        private readonly IModel channel;
        private readonly string exchangeName;
        private readonly Func<Message, Task<ProcessMessageResult>> process;
        private readonly ILogger<RabbitService> logger;

        private bool disposed;
        private string? queueName;
        private string? consumerTag;

        public MessageConsumer(
            IModel channel,
            string exchangeName,
            Func<Message, Task<ProcessMessageResult>> process,
            ILogger<RabbitService> logger)
        {
            this.channel = channel;
            this.exchangeName = exchangeName;
            this.process = process;
            this.logger = logger;
        }

        public void Initialize()
        {
            var queue = channel.QueueDeclare();
            queueName = queue.QueueName;
            logger.QueueInitialized(queueName);

            channel.QueueBind(queueName, exchangeName, string.Empty);
            logger.QueueBound(queueName, exchangeName);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += ConsumerOnReceived;
            consumerTag = channel.BasicConsume(queueName, false, consumer);

            logger.ConsumerInitialized(exchangeName, queueName, consumerTag);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (consumerTag != null)
                {
                    channel.BasicCancel(consumerTag);
                    logger.ConsumerStopped(exchangeName, queueName!, consumerTag);
                    consumerTag = null;
                }

                channel.Close();
                channel.Dispose();
                disposed = true;
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "All errors are handled by returning the message back to queue.")]
        private async Task ConsumerOnReceived(object? sender, BasicDeliverEventArgs e)
        {
            try
            {
                logger.ConsumingMessage(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);

                var message = new Message(e.Body, e.BasicProperties);
                var result = await process(message);

                switch (result)
                {
                    case ProcessMessageResult.Success:
                        channel.BasicAck(e.DeliveryTag, false);
                        logger.MessageConsumed(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    case ProcessMessageResult.Reject:
                        channel.BasicReject(e.DeliveryTag, false);
                        logger.MessageRejected(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    case ProcessMessageResult.Requeue:
                        channel.BasicNack(e.DeliveryTag, false, true);
                        logger.MessageRequeued(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown ProcessMessageResult value {result}.");
                }
            }
            catch (Exception ex)
            {
                logger.ErrorConsumingMessage(ex, exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                channel.BasicNack(e.DeliveryTag, false, true);
            }
        }
    }
}
