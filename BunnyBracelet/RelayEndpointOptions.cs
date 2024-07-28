namespace BunnyBracelet;

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
