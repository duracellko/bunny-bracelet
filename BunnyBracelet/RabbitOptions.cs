namespace BunnyBracelet;

public class RabbitOptions
{
    private static readonly Uri DefaultRabbitMQUri = new Uri("ampq://localhost/");

    public Uri RabbitMQUri { get; set; } = DefaultRabbitMQUri;

    public string? InboundExchange { get; set; }

    public string? OutboundExchange { get; set; }
}
