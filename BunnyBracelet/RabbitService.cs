using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BunnyBracelet;

/// <summary>
/// The object provides simple abstract operations to send a <see cref="Message"/>
/// to RabbitMQ <see cref="RabbitOptions.InboundExchange"/> and consume Messages
/// from <see cref="RabbitOptions.OutboundExchange"/>.
/// </summary>
public sealed class RabbitService : IDisposable
{
    private readonly IOptions<RabbitOptions> options;
    private readonly ILogger<RabbitService> logger;

    // Lazy<T> cannot be used, because it caches exception.
    private readonly object connectionLock = new object();
    private IConnection? connectionStore;
    private IModel? sendChannelStore;
    private bool disposed;
    private volatile bool disposing;

    public RabbitService(IOptions<RabbitOptions> options, ILogger<RabbitService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.options = options;
        this.logger = logger;
    }

    public bool IsConnected
    {
        get
        {
            var connection = connectionStore;
            if (connection is null)
            {
                connection = Volatile.Read(ref connectionStore);
            }

            return connection is not null && connection.IsOpen;
        }
    }

    private IConnection Connection
    {
        get
        {
            if (connectionStore is null)
            {
                lock (connectionLock)
                {
                    if (connectionStore is null)
                    {
                        connectionStore = CreateConnection();
                    }
                }
            }

            return connectionStore;
        }
    }

    /// <summary>
    /// Gets a RabbitMQ connection channel for sending messages to RabbitMQ.
    /// Single channel is used to send all messages. Therefore, sending messages
    /// is not thread-safe. Consumer of this object must ensure to not send
    /// 2 messages in parallel.
    /// </summary>
    private IModel SendChannel
    {
        get
        {
            // Ensure that connection is opened.
            var connection = Connection;

            if (sendChannelStore is null)
            {
                // Reusing the connectionLock. SendChannel is used only after
                // the connection is created and then connectionLock is not
                // required by connectionStore.
                lock (connectionLock)
                {
                    if (sendChannelStore is null)
                    {
                        sendChannelStore = CreateSendChannel(connection);
                    }
                }
            }

            return sendChannelStore;
        }
    }

    public void SendMessage(Message message)
    {
        CheckDisposed();

        var exchange = options.Value.InboundExchange?.Name;
        if (string.IsNullOrEmpty(exchange))
        {
            throw new InvalidOperationException("Inbound exchange must be specified.");
        }

        logger.PublishingMessage(exchange, message.Properties, message.Body.Length);

        try
        {
            SendChannel.BasicPublish(exchange, string.Empty, message.Properties, message.Body);
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

        return SendChannel.CreateBasicProperties();
    }

    public IDisposable ConsumeMessages(Func<Message, Task<ProcessMessageResult>> process, RabbitQueueOptions? queueOptions)
    {
        ArgumentNullException.ThrowIfNull(process);
        CheckDisposed();

        var exchangeOptions = options.Value.OutboundExchange;
        var exchangeName = exchangeOptions?.Name;
        if (exchangeOptions is null || string.IsNullOrEmpty(exchangeName))
        {
            throw new InvalidOperationException("Outbound Exchange must be specified.");
        }

        logger.InitializingConsumer(exchangeName);

        // Create separate channel for each consumer.
        var channel = Connection.CreateModel();
        try
        {
            channel.ExchangeDeclare(
                exchangeName,
                exchangeOptions.Type,
                exchangeOptions.Durable,
                exchangeOptions.AutoDelete,
                null);
            logger.ExchangeInitialized(exchangeName, exchangeOptions.Type, exchangeOptions.Durable);

            var messageConsumer = new MessageConsumer(channel, process, exchangeName, queueOptions, logger);
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

            var connection = connectionStore;
            if (connection is not null)
            {
                connection.Close();
                connection.Dispose();
            }

            var sendChannel = sendChannelStore;
            if (sendChannel is not null)
            {
                // No need to close the channel. All channels are closed with connection.
                sendChannel.Dispose();
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

    private IModel CreateSendChannel(IConnection connection)
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var exchangeOptions = options.Value.InboundExchange;
        System.Diagnostics.Debug.Assert(exchangeOptions is not null, "Inbound Exchange is not specified.");
        var exchangeName = exchangeOptions.Name;
        System.Diagnostics.Debug.Assert(!string.IsNullOrEmpty(exchangeName), "Inbound Exchange name is not specified.");

        var channel = connection.CreateModel();
        channel.ExchangeDeclare(
            exchangeName,
            exchangeOptions.Type,
            exchangeOptions.Durable,
            exchangeOptions.AutoDelete,
            null);
        logger.ExchangeInitialized(exchangeName, exchangeOptions.Type, exchangeOptions.Durable);
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

    /// <summary>
    /// Message consumer that consumes messages from specified exchange or queue.
    /// Each message is sent to the specified delegate function. The function should
    /// respond, whether the message should be acknowledged, rejected, or returned
    /// back to the queue.
    /// Consuming of messages is stopped by disposing this object.
    /// </summary>
    private sealed class MessageConsumer : IDisposable
    {
        private readonly IModel channel;
        private readonly Func<Message, Task<ProcessMessageResult>> process;
        private readonly string exchangeName;
        private readonly RabbitQueueOptions? queueOptions;
        private readonly ILogger<RabbitService> logger;

        private bool disposed;
        private string? queueName;
        private string? consumerTag;

        public MessageConsumer(
            IModel channel,
            Func<Message, Task<ProcessMessageResult>> process,
            string exchangeName,
            RabbitQueueOptions? queueOptions,
            ILogger<RabbitService> logger)
        {
            this.channel = channel;
            this.process = process;
            this.exchangeName = exchangeName;
            this.queueOptions = queueOptions;
            this.logger = logger;
        }

        public void Initialize()
        {
            var durable = queueOptions?.Durable ?? false;
            var autoDelete = queueOptions?.AutoDelete ?? true;
            var queue = channel.QueueDeclare(
                queueOptions?.Name ?? string.Empty,
                durable,
                autoDelete,
                autoDelete,
                null);
            queueName = queue.QueueName;
            logger.QueueInitialized(queueName, durable);

            channel.QueueBind(queueName, exchangeName, string.Empty);
            logger.QueueBound(queueName, exchangeName);

            // Receive only single message at time, so that it is returned to queue head
            // in case of error.
            channel.BasicQos(0, 1, false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += ConsumerOnReceived;
            consumerTag = channel.BasicConsume(queueName, false, consumer);

            logger.ConsumerInitialized(exchangeName, queueName, consumerTag);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (consumerTag != null && channel.IsOpen)
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
                channel.BasicNack(e.DeliveryTag, false, !e.Redelivered);
            }
        }
    }
}
