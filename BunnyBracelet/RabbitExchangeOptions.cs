using RabbitMQ.Client;

namespace BunnyBracelet;

public class RabbitExchangeOptions
{
    /// <summary>
    /// Gets or sets the name of the RabbitMQ exchange.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the type of the RabbitMQ exchange.
    /// </summary>
    /// <remarks>
    /// Default value is "fanout".
    /// </remarks>
    public string Type { get; set; } = ExchangeType.Fanout;

    /// <summary>
    /// Gets or sets a value indicating whether the exchange should be durable.
    /// </summary>
    /// <remarks>
    /// Default value is false.
    /// </remarks>
    public bool Durable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the exchange should be automatically deleted.
    /// </summary>
    /// <remarks>
    /// Default value is false.
    /// </remarks>
    public bool AutoDelete { get; set; }
}
