namespace BunnyBracelet.SystemTests;

internal sealed class ExchangeSettings
{
    public string? Name { get; set; }

    public string? Type { get; set; }

    public bool? Durable { get; set; }

    public bool? AutoDelete { get; set; }

    public IDictionary<string, string?> Arguments { get; } = new Dictionary<string, string?>();
}
