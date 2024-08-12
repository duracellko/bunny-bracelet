using RabbitMQ.Client;

namespace BunnyBracelet;

internal static class BunnyBraceletLogger
{
    private static readonly Action<ILogger, long?, string?, Exception?> LogReceivingInboundMessage = LoggerMessage.Define<long?, string?>(
        LogLevel.Debug,
        new EventId(1, nameof(ReceivingInboundMessage)),
        "Receiving inbound message with request size {RequestSize}. (TraceIdentifier: {TraceIdentifier})");

    private static readonly Action<ILogger, string?, string?, int, string?, Exception?> LogInboundMessageForwarded = LoggerMessage.Define<string?, string?, int, string?>(
        LogLevel.Information,
        new EventId(2, nameof(InboundMessageForwarded)),
        "Inbound message (MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}) was forwarded to RabbitMQ. (TraceIdentifier: {TraceIdentifier})");

    private static readonly Action<ILogger, string?, Exception?> LogErrorReadingInboundMessage = LoggerMessage.Define<string?>(
        LogLevel.Error,
        new EventId(3, nameof(ErrorReadingInboundMessage)),
        "Reading inbound message failed. (TraceIdentifier: {TraceIdentifier})");

    private static readonly Action<ILogger, string?, string?, int?, string?, Exception?> LogErrorProcessingInboundMessage = LoggerMessage.Define<string?, string?, int?, string?>(
        LogLevel.Error,
        new EventId(4, nameof(ErrorProcessingInboundMessage)),
        "Processing inbound message (MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}) failed. (TraceIdentifier: {TraceIdentifier})");

    private static readonly Action<ILogger, Exception?> LogMissingInboundExchange = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5, nameof(MissingInboundExchange)),
        "Inbound exchange is not configured. Receiving of messages is disabled. Configure setting 'BunnyBracelet.InboundExchange'.");

    private static readonly Action<ILogger, DateTime, Exception?> LogErrorMessageTimestampAuthentication = LoggerMessage.Define<DateTime>(
        LogLevel.Error,
        new EventId(6, nameof(ErrorMessageTimestampAuthentication)),
        "Message authentication failed. Timestamp '{Timestamp}' is expired.");

    private static readonly Action<ILogger, string?, string?, int, string, Uri, Exception?> LogRelayingMessage = LoggerMessage.Define<string?, string?, int, string, Uri>(
        LogLevel.Debug,
        new EventId(100, nameof(RelayingMessage)),
        "Relying message (MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}) from RabbitMQ exchange '{Exchange}' to '{Uri}'.");

    private static readonly Action<ILogger, string?, string?, int, string, Uri, Exception?> LogMessageRelayed = LoggerMessage.Define<string?, string?, int, string, Uri>(
        LogLevel.Information,
        new EventId(101, nameof(MessageRelayed)),
        "Message (MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}) was relayed from RabbitMQ exchange '{Exchange}' to '{Uri}'.");

    private static readonly Action<ILogger, string?, string?, int, string, Uri, string?, Exception?> LogErrorRelayingMessage = LoggerMessage.Define<string?, string?, int, string, Uri, string?>(
        LogLevel.Error,
        new EventId(102, nameof(ErrorRelayingMessage)),
        "Relaying message (MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}) from RabbitMQ exchange '{Exchange}' to '{Uri}' failed. Response: {Response}");

    private static readonly Action<ILogger, Exception?> LogRelayServiceStarting = LoggerMessage.Define(
        LogLevel.Debug,
        new EventId(103, nameof(RelayServiceStarting)),
        "Relay Service is starting.");

    private static readonly Action<ILogger, Exception?> LogRelayServiceStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(104, nameof(RelayServiceStarted)),
        "Relay Service started.");

    private static readonly Action<ILogger, Exception?> LogRelayServiceStopping = LoggerMessage.Define(
        LogLevel.Debug,
        new EventId(105, nameof(RelayServiceStopping)),
        "Relay Service is stopping.");

    private static readonly Action<ILogger, Exception?> LogRelayServiceStopped = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(106, nameof(RelayServiceStopped)),
        "Relay Service stopped.");

    private static readonly Action<ILogger, string, string?, Uri, Exception?> LogRelayEndpointConfigured = LoggerMessage.Define<string, string?, Uri>(
        LogLevel.Information,
        new EventId(107, nameof(RelayEndpointConfigured)),
        "Relay from RabbitMQ exchange '{Exchange}' and queue '{Queue}' to endpoint '{Uri}' is setup.");

    private static readonly Action<ILogger, string, string?, Uri, Exception?> LogErrorConfiguringRelayEndpoint = LoggerMessage.Define<string, string?, Uri>(
        LogLevel.Error,
        new EventId(108, nameof(ErrorConfiguringRelayEndpoint)),
        "Setup of relay from RabbitMQ exchange '{Exchange}' and queue '{Queue}' to endpoint '{Uri}' failed.");

    private static readonly Action<ILogger, Exception?> LogMissingOutboundExchange = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(109, nameof(MissingOutboundExchange)),
        "Outbound exchange is not configured. Message relay service is disabled. Configure setting 'BunnyBracelet.OutboundExchange'.");

    private static readonly Action<ILogger, Exception?> LogErrorClosingRelayConsumer = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(110, nameof(ErrorClosingRelayConsumer)),
        "Closing RabbitMQ consumer failed.");

    private static readonly Action<ILogger, Uri, Exception?> LogConnectingToRabbitMQ = LoggerMessage.Define<Uri>(
        LogLevel.Information,
        new EventId(200, nameof(ConnectingToRabbitMQ)),
        "Connecting to RabbitMQ: {Uri}");

    private static readonly Action<ILogger, string, string?, string?, string?, int, Exception?> LogPublishingMessage = LoggerMessage.Define<string, string?, string?, string?, int>(
        LogLevel.Debug,
        new EventId(201, nameof(PublishingMessage)),
        "Publishing message to exchange '{Exchange}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Type: {Type}, Size: {Size}");

    private static readonly Action<ILogger, string, string?, string?, string?, int, Exception?> LogMessagePublished = LoggerMessage.Define<string, string?, string?, string?, int>(
        LogLevel.Information,
        new EventId(202, nameof(MessagePublished)),
        "Message published to exchange '{Exchange}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Type: {Type}, Size: {Size}");

    private static readonly Action<ILogger, string, string?, string?, string?, int, Exception?> LogErrorPublishingMessage = LoggerMessage.Define<string, string?, string?, string?, int>(
        LogLevel.Error,
        new EventId(203, nameof(ErrorPublishingMessage)),
        "Publishing message to exchange '{Exchange}' failed. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Type: {Type}, Size: {Size}");

    private static readonly Action<ILogger, string, Exception?> LogInitializingConsumer = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(204, nameof(InitializingConsumer)),
        "Initializing consumer on RabbitMQ exchange '{Exchange}'.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogConsumerInitialized = LoggerMessage.Define<string, string, string>(
        LogLevel.Information,
        new EventId(205, nameof(ConsumerInitialized)),
        "Consumer on RabbitMQ exchange '{Exchange}' and queue '{Queue}' is initialized with tag '{ConsumerTag}'.");

    private static readonly Action<ILogger, string, Exception?> LogErrorInitializingConsumer = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(206, nameof(ErrorInitializingConsumer)),
        "Initializing consumer on RabbitMQ exchange '{Exchange}' failed.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogConsumerStopped = LoggerMessage.Define<string, string, string>(
        LogLevel.Information,
        new EventId(207, nameof(ConsumerStopped)),
        "Consumer with tag '{ConsumerTag}' stopped on RabbitMQ exchange '{Exchange}' and queue '{Queue}'.");

    private static readonly Action<ILogger, string, string, string, string?, string?, int, Exception?> LogConsumingMessage = LoggerMessage.Define<string, string, string, string?, string?, int>(
        LogLevel.Debug,
        new EventId(208, nameof(ConsumingMessage)),
        "Consuming message by consumer tag '{ConsumerTag}' from exchange '{Exchange}' and queue '{Queue}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}");

    private static readonly Action<ILogger, string, string, string, string?, string?, int, Exception?> LogMessageConsumed = LoggerMessage.Define<string, string, string, string?, string?, int>(
        LogLevel.Information,
        new EventId(209, nameof(MessageConsumed)),
        "Message consumed by consumer tag '{ConsumerTag}' from exchange '{Exchange}' and queue '{Queue}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}");

    private static readonly Action<ILogger, string, string, string, string?, string?, int, Exception?> LogMessageRejected = LoggerMessage.Define<string, string, string, string?, string?, int>(
        LogLevel.Warning,
        new EventId(210, nameof(MessageRejected)),
        "Message rejected by consumer tag '{ConsumerTag}' from exchange '{Exchange}' and queue '{Queue}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}");

    private static readonly Action<ILogger, string, string, string, string?, string?, int, Exception?> LogMessageRequeued = LoggerMessage.Define<string, string, string, string?, string?, int>(
        LogLevel.Warning,
        new EventId(211, nameof(MessageRequeued)),
        "Message requeued by consumer tag '{ConsumerTag}' from exchange '{Exchange}' and queue '{Queue}'. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}");

    private static readonly Action<ILogger, string, string, string, string?, string?, int, Exception?> LogErrorConsumingMessage = LoggerMessage.Define<string, string, string, string?, string?, int>(
        LogLevel.Error,
        new EventId(212, nameof(ErrorConsumingMessage)),
        "Consuming message by consumer tag '{ConsumerTag}' from exchange '{Exchange}' and queue '{Queue}' failed. MessageId: {MessageId}, CorrelationId: {CorrelationId}, Size: {Size}");

    private static readonly Action<ILogger, string, string, bool, Exception?> LogExchangeInitialized = LoggerMessage.Define<string, string, bool>(
        LogLevel.Information,
        new EventId(213, nameof(ExchangeInitialized)),
        "Exchange '{Exchange}' is initialized. Type: {Type}, Durable: {Durable}");

    private static readonly Action<ILogger, string, bool, Exception?> LogQueueInitialized = LoggerMessage.Define<string, bool>(
        LogLevel.Information,
        new EventId(214, nameof(QueueInitialized)),
        "Queue '{Queue}' is initialized. Durable: {Durable}");

    private static readonly Action<ILogger, string, string, Exception?> LogQueueBound = LoggerMessage.Define<string, string>(
        LogLevel.Information,
        new EventId(215, nameof(QueueBound)),
        "Queue '{Queue}' is bound to exchange '{Exchange}'.");

    private static readonly Action<ILogger, Exception?> LogErrorConnectionCallback = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(216, nameof(ErrorConnectionCallback)),
        "Unhandled error occured in RabbitMQ callback/consumer.");

    public static void ReceivingInboundMessage(this ILogger logger, long? size, string? traceIdentifier)
    {
        LogReceivingInboundMessage(logger, size, traceIdentifier, null);
    }

    public static void InboundMessageForwarded(this ILogger logger, IBasicProperties? properties, int size, string? traceIdentifier)
    {
        LogInboundMessageForwarded(logger, properties?.MessageId, properties?.CorrelationId, size, traceIdentifier, null);
    }

    public static void ErrorReadingInboundMessage(this ILogger logger, Exception exception, string? traceIdentifier)
    {
        LogErrorReadingInboundMessage(logger, traceIdentifier, exception);
    }

    public static void ErrorProcessingInboundMessage(this ILogger logger, Exception exception, IBasicProperties? properties, int? size, string? traceIdentifier)
    {
        LogErrorProcessingInboundMessage(logger, properties?.MessageId, properties?.CorrelationId, size, traceIdentifier, exception);
    }

    public static void MissingInboundExchange(this ILogger logger)
    {
        LogMissingInboundExchange(logger, null);
    }

    public static void ErrorMessageTimestampAuthentication(this ILogger logger, DateTime timestamp)
    {
        LogErrorMessageTimestampAuthentication(logger, timestamp, null);
    }

    public static void RelayingMessage(this ILogger logger, Uri uri, string exchange, IBasicProperties? properties, int size)
    {
        LogRelayingMessage(logger, properties?.MessageId, properties?.CorrelationId, size, exchange, uri, null);
    }

    public static void MessageRelayed(this ILogger logger, Uri uri, string exchange, IBasicProperties? properties, int size)
    {
        LogMessageRelayed(logger, properties?.MessageId, properties?.CorrelationId, size, exchange, uri, null);
    }

    public static void ErrorRelayingMessage(this ILogger logger, Exception exception, Uri uri, string exchange, IBasicProperties? properties, int size, string? response)
    {
        LogErrorRelayingMessage(logger, properties?.MessageId, properties?.CorrelationId, size, exchange, uri, response, exception);
    }

    public static void RelayServiceStarting(this ILogger logger)
    {
        LogRelayServiceStarting(logger, null);
    }

    public static void RelayServiceStarted(this ILogger logger)
    {
        LogRelayServiceStarted(logger, null);
    }

    public static void RelayServiceStopping(this ILogger logger)
    {
        LogRelayServiceStopping(logger, null);
    }

    public static void RelayServiceStopped(this ILogger logger)
    {
        LogRelayServiceStopped(logger, null);
    }

    public static void RelayEndpointConfigured(this ILogger logger, Uri uri, string exchange, string? queue)
    {
        LogRelayEndpointConfigured(logger, exchange, queue, uri, null);
    }

    public static void ErrorConfiguringRelayEndpoint(this ILogger logger, Exception exception, Uri uri, string exchange, string? queue)
    {
        LogErrorConfiguringRelayEndpoint(logger, exchange, queue, uri, exception);
    }

    public static void MissingOutboundExchange(this ILogger logger)
    {
        LogMissingOutboundExchange(logger, null);
    }

    public static void ErrorClosingRelayConsumer(this ILogger logger, Exception exception)
    {
        LogErrorClosingRelayConsumer(logger, exception);
    }

    public static void ConnectingToRabbitMQ(this ILogger logger, Uri uri)
    {
        if (uri.UserInfo.Contains(':'))
        {
            var uriBuilder = new UriBuilder(uri);
            uriBuilder.Password = null;
            uri = uriBuilder.Uri;
        }

        LogConnectingToRabbitMQ(logger, uri, null);
    }

    public static void PublishingMessage(this ILogger logger, string exchange, IBasicProperties? properties, int size)
    {
        LogPublishingMessage(logger, exchange, properties?.MessageId, properties?.CorrelationId, properties?.Type, size, null);
    }

    public static void MessagePublished(this ILogger logger, string exchange, IBasicProperties? properties, int size)
    {
        LogMessagePublished(logger, exchange, properties?.MessageId, properties?.CorrelationId, properties?.Type, size, null);
    }

    public static void ErrorPublishingMessage(this ILogger logger, Exception exception, string exchange, IBasicProperties? properties, int size)
    {
        LogErrorPublishingMessage(logger, exchange, properties?.MessageId, properties?.CorrelationId, properties?.Type, size, exception);
    }

    public static void InitializingConsumer(this ILogger logger, string exchange)
    {
        LogInitializingConsumer(logger, exchange, null);
    }

    public static void ConsumerInitialized(this ILogger logger, string exchange, string queue, string consumerTag)
    {
        LogConsumerInitialized(logger, exchange, queue, consumerTag, null);
    }

    public static void ErrorInitializingConsumer(this ILogger logger, Exception exception, string exchange)
    {
        LogErrorInitializingConsumer(logger, exchange, exception);
    }

    public static void ConsumerStopped(this ILogger logger, string exchange, string queue, string consumerTag)
    {
        LogConsumerStopped(logger, consumerTag, exchange, queue, null);
    }

    public static void ConsumingMessage(this ILogger logger, string exchange, string queue, string consumerTag, IBasicProperties? properties, int size)
    {
        LogConsumingMessage(logger, consumerTag, exchange, queue, properties?.MessageId, properties?.CorrelationId, size, null);
    }

    public static void MessageConsumed(this ILogger logger, string exchange, string queue, string consumerTag, IBasicProperties? properties, int size)
    {
        LogMessageConsumed(logger, consumerTag, exchange, queue, properties?.MessageId, properties?.CorrelationId, size, null);
    }

    public static void MessageRejected(this ILogger logger, string exchange, string queue, string consumerTag, IBasicProperties? properties, int size)
    {
        LogMessageRejected(logger, consumerTag, exchange, queue, properties?.MessageId, properties?.CorrelationId, size, null);
    }

    public static void MessageRequeued(this ILogger logger, string exchange, string queue, string consumerTag, IBasicProperties? properties, int size)
    {
        LogMessageRequeued(logger, consumerTag, exchange, queue, properties?.MessageId, properties?.CorrelationId, size, null);
    }

    public static void ErrorConsumingMessage(this ILogger logger, Exception exception, string exchange, string queue, string consumerTag, IBasicProperties? properties, int size)
    {
        LogErrorConsumingMessage(logger, consumerTag, exchange, queue, properties?.MessageId, properties?.CorrelationId, size, exception);
    }

    public static void ExchangeInitialized(this ILogger logger, string exchange, string type, bool durable)
    {
        LogExchangeInitialized(logger, exchange, type, durable, null);
    }

    public static void QueueInitialized(this ILogger logger, string queue, bool durable)
    {
        LogQueueInitialized(logger, queue, durable, null);
    }

    public static void QueueBound(this ILogger logger, string queue, string exchange)
    {
        LogQueueBound(logger, queue, exchange, null);
    }

    public static void ErrorConnectionCallback(this ILogger logger, Exception exception)
    {
        LogErrorConnectionCallback(logger, exception);
    }
}
