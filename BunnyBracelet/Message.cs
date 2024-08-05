using RabbitMQ.Client;

namespace BunnyBracelet;

/// <summary>
/// This value object represents a Message that could be put into a RabbitMQ queue
/// or picked up from a queue. The message includes metadata (<see cref="Properties"/>),
/// custom headers, and binary content (<see cref="Body"/>).
/// </summary>
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
