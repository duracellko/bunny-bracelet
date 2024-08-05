namespace BunnyBracelet;

/// <summary>
/// Exception represents an error reading <see cref="Message"/> from a Stream.
/// </summary>
public class MessageException : Exception
{
    public MessageException()
    {
    }

    public MessageException(string message)
        : base(message)
    {
    }

    public MessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
