namespace BunnyBracelet;

/// <summary>
/// Configuration BunnyBracelet message relay service.
/// </summary>
public class RelayOptions
{
    /// <summary>
    /// Gets a collection of endpoints that the messages
    /// from <see cref="RabbitOptions.OutboundExchange"/> should be relayed to.
    /// </summary>
    public IList<RelayEndpointOptions> Endpoints { get; } = new List<RelayEndpointOptions>();

    /// <summary>
    /// Gets or sets the timeout in milliseconds for sending a message to an HTTP endpoint.
    /// </summary>
    /// <remarks>
    /// Default value is 30000ms.
    /// </remarks>
    public int Timeout { get; set; } = 30000;

    /// <summary>
    /// Gets or sets the delay time in millisecond between relay failure and
    /// returning the message back to the queue.
    /// </summary>
    /// <remarks>
    /// Default value is 1000ms.
    /// </remarks>
    public int RequeueDelay { get; set; } = 1000;
}
