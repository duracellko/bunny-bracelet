namespace BunnyBracelet.SystemTests;

internal sealed class EndpointSettings
{
    public string? Uri { get; set; }

    public string? QueueName { get; set; }

    public bool? Durable { get; set; }

    public bool? AutoDelete { get; set; }
}
