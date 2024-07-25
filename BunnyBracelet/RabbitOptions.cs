namespace BunnyBracelet;

public class RabbitOptions
{
    public Uri? RabbitMQUri { get; set; }

    public string? InboundExchange { get; set; }

    public string? OutboundExchange { get; set; }
}
