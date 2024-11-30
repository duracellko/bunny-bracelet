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
public sealed class RabbitService : IAsyncDisposable, IDisposable
{
    private readonly IOptions<RabbitOptions> options;
    private readonly ILogger<RabbitService> logger;

    private readonly SemaphoreSlim connectionFactorySemaphore = new(1);
    private IConnection? connectionStore;
    private IChannel? sendChannelStore;
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
            connection ??= Volatile.Read(ref connectionStore);
            return connection is not null && connection.IsOpen;
        }
    }

    public async ValueTask SendMessage(Message message, CancellationToken cancellationToken = default)
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
            var sendChannel = await GetSendChannel(cancellationToken);

            if (message.Properties is not null)
            {
                var basicProperties = new BasicProperties(message.Properties);
                await sendChannel.BasicPublishAsync(exchange, string.Empty, false, basicProperties, message.Body, cancellationToken);
            }
            else
            {
                await sendChannel.BasicPublishAsync(exchange, string.Empty, false, message.Body, cancellationToken);
            }

            logger.MessagePublished(exchange, message.Properties, message.Body.Length);
        }
        catch (Exception ex)
        {
            logger.ErrorPublishingMessage(ex, exchange, message.Properties, message.Body.Length);
            throw;
        }
    }

    public async ValueTask<IAsyncDisposable> ConsumeMessages(
        Func<Message, CancellationToken, Task<ProcessMessageResult>> process,
        RabbitQueueOptions? queueOptions,
        CancellationToken cancellationToken = default)
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
        var connection = await GetConnection(cancellationToken);
        var channel = await connection.CreateChannelAsync(null, cancellationToken);
        try
        {
            await channel.ExchangeDeclareAsync(
                exchangeName,
                exchangeOptions.Type,
                exchangeOptions.Durable,
                exchangeOptions.AutoDelete,
                cancellationToken: cancellationToken);
            logger.ExchangeInitialized(exchangeName, exchangeOptions.Type, exchangeOptions.Durable);

            var messageConsumer = new MessageConsumer(channel, process, exchangeName, queueOptions, logger);
            await messageConsumer.Initialize(cancellationToken);
            return messageConsumer;
        }
        catch (Exception ex)
        {
            logger.ErrorInitializingConsumer(ex, exchangeName);
            await channel.CloseAsync(cancellationToken);
            await channel.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposing = true;

            var connection = connectionStore;
            if (connection is not null)
            {
                await connection.CloseAsync();
                await connection.DisposeAsync();
            }

            // No need to close the channel. All channels are closed with connection.
            var sendChannel = sendChannelStore;
            if (sendChannel is not null)
            {
                await sendChannel.DisposeAsync();
            }

            connectionFactorySemaphore.Dispose();

            disposed = true;
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposing = true;

            // Synchronous Dispose does not wait for closing connection, just disposes the connection directly.
            var connection = connectionStore;
            connection?.Dispose();

            var sendChannel = sendChannelStore;
            sendChannel?.Dispose();

            connectionFactorySemaphore.Dispose();

            disposed = true;
        }
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Connection can be changed by a different thread.")]
    private async ValueTask<IConnection> GetConnection(CancellationToken cancellationToken)
    {
        if (connectionStore is null)
        {
            await connectionFactorySemaphore.WaitAsync(cancellationToken);
            try
            {
                connectionStore ??= await CreateConnection(cancellationToken);
            }
            finally
            {
                connectionFactorySemaphore.Release();
            }
        }

        return connectionStore;
    }

    [SuppressMessage("Style", "IDE0270:Use coalesce expression", Justification = "Throwing exception should not be simplified.")]
    private async ValueTask<IConnection> CreateConnection(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var uri = options.Value.RabbitMQUri;
        if (uri is null)
        {
            throw new InvalidOperationException("RabbitMQ URI must be specified.");
        }

        var connectionFactory = new ConnectionFactory
        {
            Uri = uri
        };

        logger.ConnectingToRabbitMQ(uri);

        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        connection.CallbackExceptionAsync += ConnectionOnCallbackException;
        return connection;
    }

    /// <summary>
    /// Gets a RabbitMQ connection channel for sending messages to RabbitMQ.
    /// Single channel is used to send all messages. Therefore, sending messages
    /// is not thread-safe. Consumer of this object must ensure to not send
    /// 2 messages in parallel.
    /// </summary>
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Channel can be changed by a different thread.")]
    private async ValueTask<IChannel> GetSendChannel(CancellationToken cancellationToken)
    {
        // Ensure that connection is opened.
        var connection = await GetConnection(cancellationToken);

        if (sendChannelStore is null)
        {
            // Reusing the connectionLock. SendChannel is used only after
            // the connection is created and then connectionLock is not
            // required by connectionStore.
            await connectionFactorySemaphore.WaitAsync(cancellationToken);
            try
            {
                sendChannelStore ??= await CreateSendChannel(connection, cancellationToken);
            }
            finally
            {
                connectionFactorySemaphore.Release();
            }
        }

        return sendChannelStore;
    }

    private async ValueTask<IChannel> CreateSendChannel(IConnection connection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposing, this);

        var exchangeOptions = options.Value.InboundExchange;
        System.Diagnostics.Debug.Assert(exchangeOptions is not null, "Inbound Exchange is not specified.");
        var exchangeName = exchangeOptions.Name;
        System.Diagnostics.Debug.Assert(!string.IsNullOrEmpty(exchangeName), "Inbound Exchange name is not specified.");

        var channel = await connection.CreateChannelAsync(null, cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchangeName,
            exchangeOptions.Type,
            exchangeOptions.Durable,
            exchangeOptions.AutoDelete,
            cancellationToken: cancellationToken);
        logger.ExchangeInitialized(exchangeName, exchangeOptions.Type, exchangeOptions.Durable);
        return channel;
    }

    private Task ConnectionOnCallbackException(object sender, CallbackExceptionEventArgs e)
    {
        logger.ErrorConnectionCallback(e.Exception);
        return Task.CompletedTask;
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
    private sealed class MessageConsumer : IAsyncDisposable
    {
        private readonly IChannel channel;
        private readonly Func<Message, CancellationToken, Task<ProcessMessageResult>> process;
        private readonly string exchangeName;
        private readonly RabbitQueueOptions? queueOptions;
        private readonly ILogger<RabbitService> logger;

        private bool disposed;
        private string? queueName;
        private string? consumerTag;

        public MessageConsumer(
            IChannel channel,
            Func<Message, CancellationToken, Task<ProcessMessageResult>> process,
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

        public async ValueTask Initialize(CancellationToken cancellationToken)
        {
            var durable = queueOptions?.Durable ?? false;
            var autoDelete = queueOptions?.AutoDelete ?? true;
            var queue = await channel.QueueDeclareAsync(
                queueOptions?.Name ?? string.Empty,
                durable,
                autoDelete,
                autoDelete,
                cancellationToken: cancellationToken);
            queueName = queue.QueueName;
            logger.QueueInitialized(queueName, durable);

            await channel.QueueBindAsync(queueName, exchangeName, string.Empty, cancellationToken: cancellationToken);
            logger.QueueBound(queueName, exchangeName);

            // Receive only single message at time, so that it is returned to queue head
            // in case of error.
            await channel.BasicQosAsync(0, 1, false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += ConsumerOnReceived;
            consumerTag = await channel.BasicConsumeAsync(queueName, false, consumer, cancellationToken);

            logger.ConsumerInitialized(exchangeName, queueName, consumerTag);
        }

        public async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                if (consumerTag != null && channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag);
                    logger.ConsumerStopped(exchangeName, queueName!, consumerTag);
                    consumerTag = null;
                }

                await channel.CloseAsync();
                await channel.DisposeAsync();
                disposed = true;
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "All errors are handled by returning the message back to queue.")]
        private async Task ConsumerOnReceived(object? sender, BasicDeliverEventArgs e)
        {
            try
            {
                logger.ConsumingMessage(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);

                var message = new Message(e.Body, e.BasicProperties, default);
                var result = await process(message, e.CancellationToken);

                switch (result)
                {
                    case ProcessMessageResult.Success:
                        await channel.BasicAckAsync(e.DeliveryTag, false, e.CancellationToken);
                        logger.MessageConsumed(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    case ProcessMessageResult.Reject:
                        await channel.BasicRejectAsync(e.DeliveryTag, false, e.CancellationToken);
                        logger.MessageRejected(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    case ProcessMessageResult.Requeue:
                        await channel.BasicNackAsync(e.DeliveryTag, false, true, e.CancellationToken);
                        logger.MessageRequeued(exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown ProcessMessageResult value {result}.");
                }
            }
            catch (Exception ex)
            {
                logger.ErrorConsumingMessage(ex, exchangeName, queueName!, consumerTag!, e.BasicProperties, e.Body.Length);
                await channel.BasicNackAsync(e.DeliveryTag, false, !e.Redelivered, e.CancellationToken);
            }
        }
    }
}
