namespace BunnyBracelet;

/// <summary>
/// Result of processing of a message from RabbitMQ queue or
/// relaying a message to BunnyBracelet instance.
/// </summary>
public enum ProcessMessageResult
{
    Success,
    Reject,
    Requeue
}
