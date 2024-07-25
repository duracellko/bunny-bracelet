using RabbitMQ.Client;

namespace BunnyBracelet;

public struct Message : IEquatable<Message>
{
    public Message(ReadOnlyMemory<byte> body, IBasicProperties? properties)
    {
        Body = body;
        Properties = properties;
    }

    public ReadOnlyMemory<byte> Body { get; }

    public IBasicProperties? Properties { get; }

    public static bool operator ==(Message left, Message right) => left.Equals(right);

    public static bool operator !=(Message left, Message right) => !(left == right);

    public bool Equals(Message other)
    {
        return Body.Equals(other.Body) && Properties == other.Properties;
    }

    public override bool Equals(object? obj) => obj is Message other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Body, Properties);
}
