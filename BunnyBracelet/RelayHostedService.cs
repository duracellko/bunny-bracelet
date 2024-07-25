using Microsoft.Extensions.Options;

namespace BunnyBracelet;

public class RelayHostedService : IHostedService
{
    private readonly RabbitService rabbitService;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IMessageSerializer messageSerializer;
    private readonly IOptions<RelayOptions> options;
    private readonly List<IDisposable> consumers = new List<IDisposable>();

    public RelayHostedService(
        RabbitService rabbitService,
        IHttpClientFactory httpClientFactory,
        IMessageSerializer messageSerializer,
        IOptions<RelayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(rabbitService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(messageSerializer);
        ArgumentNullException.ThrowIfNull(options);

        this.rabbitService = rabbitService;
        this.httpClientFactory = httpClientFactory;
        this.messageSerializer = messageSerializer;
        this.options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in options.Value.Endpoints)
        {
            var consumer = rabbitService.ConsumeMessages(async m => await ProcessMessage(m, endpoint));
            consumers.Add(consumer);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        while (consumers.Count > 0)
        {
            var index = consumers.Count - 1;
            consumers[index].Dispose();
            consumers.RemoveAt(index);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessMessage(Message message, Uri endpoint)
    {
        using var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = endpoint;

        using var content = new StreamContent(await messageSerializer.ConvertMessageToStream(message));
        await httpClient.PostAsync(new Uri("message", UriKind.Relative), content);
    }
}
