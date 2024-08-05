using RabbitMQ.Client;

namespace BunnyBracelet;

/// <summary>
/// An object implementing this interface can deserialize a <see cref="Message"/>
/// from a Stream or serialize a Message to Stream.
/// </summary>
public interface IMessageSerializer
{
    ValueTask<Message> ReadMessage(Stream stream, Func<IBasicProperties> propertiesFactory);

    ValueTask<Stream> ConvertMessageToStream(Message message);
}
