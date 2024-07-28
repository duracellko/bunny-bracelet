namespace BunnyBracelet;

public class RabbitQueueOptions
{
    /// <summary>
    /// Gets or sets the name of the queue.
    /// When it is not specified, then an anonymous transient queue is created.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the queue should be durable.
    /// </summary>
    /// <remarks>
    /// Default value is false.
    /// </remarks>
    public bool Durable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the queue should be automatically deleted.
    /// </summary>
    /// <remarks>
    /// Default value is true.
    /// </remarks>
    public bool AutoDelete { get; set; } = true;

    /// <summary>
    /// Gets additional arguments of the queue.
    /// </summary>
    public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
}
