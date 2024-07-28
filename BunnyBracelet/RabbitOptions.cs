namespace BunnyBracelet;

public class RabbitOptions
{
    private static readonly Uri DefaultRabbitMQUri = new Uri("ampq://localhost/");

    /// <summary>
    /// Gets or sets URI of the RabbitMQ service.
    /// </summary>
    public Uri RabbitMQUri { get; set; } = DefaultRabbitMQUri;

    /// <summary>
    /// Gets or sets configuration of the RabbitMQ exchange to queue incoming relayed messages.
    /// </summary>
    public RabbitExchangeOptions? InboundExchange { get; set; }

    /// <summary>
    /// Gets or sets configuration of the RabbitMQ exchange to queue outgoing relayed messages.
    /// </summary>
    public RabbitExchangeOptions? OutboundExchange { get; set; }
}
