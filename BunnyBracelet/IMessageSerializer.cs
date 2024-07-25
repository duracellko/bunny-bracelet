using RabbitMQ.Client;

namespace BunnyBracelet
{
    public interface IMessageSerializer
    {
        ValueTask<Message> ReadMessage(Stream stream, Func<IBasicProperties> propertiesFactory);

        ValueTask<Stream> ConvertMessageToStream(Message message);
    }
}
