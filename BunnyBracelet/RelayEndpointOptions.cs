namespace BunnyBracelet;

/// <summary>
/// Configuration of a connection to another instance of BunnyBracelet.
/// <see cref="Message"/> objects should be relayed/forwarded from
/// <see cref="RabbitOptions.OutboundExchange"/> to this endpoint
/// of another BunnyBracelet.
/// </summary>
public class RelayEndpointOptions
{
    /// <summary>
    /// Gets or sets URI of the HTTP endpoint that the messages should be relayed to.
    /// </summary>
    public Uri? Uri { get; set; }

    /// <summary>
    /// Gets or sets configuration of RabbitMQ queue that is used to queue
    /// outbound messages for relay.
    /// </summary>
    /// <remarks>
    /// When it is not configured, then an anonymous transient queue is created.
    /// </remarks>
    public RabbitQueueOptions? Queue { get; set; }
}
