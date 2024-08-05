using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Options;

namespace BunnyBracelet;

/// <summary>
/// ASP.NET Core application background service that consumes
/// <see cref="Message"/> objects from configured
/// <see cref="RabbitOptions.OutboundExchange"/> and forwards them
/// to configured BunnyBracelet endpoints.
/// </summary>
public class RelayHostedService : IHostedService
{
    private readonly RabbitService rabbitService;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IMessageSerializer messageSerializer;
    private readonly IOptions<RelayOptions> options;
    private readonly IOptions<RabbitOptions> rabbitOptions;
    private readonly ILogger<RelayHostedService> logger;
    private readonly List<IDisposable> consumers = new List<IDisposable>();

    public RelayHostedService(
        RabbitService rabbitService,
        IHttpClientFactory httpClientFactory,
        IMessageSerializer messageSerializer,
        IOptions<RelayOptions> options,
        IOptions<RabbitOptions> rabbitOptions,
        ILogger<RelayHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(rabbitService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(messageSerializer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rabbitOptions);
        ArgumentNullException.ThrowIfNull(logger);

        this.rabbitService = rabbitService;
        this.httpClientFactory = httpClientFactory;
        this.messageSerializer = messageSerializer;
        this.options = options;
        this.rabbitOptions = rabbitOptions;
        this.logger = logger;
    }

    public int ConsumersCount => consumers.Count;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "On error continue setup of other endpoints.")]
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.RelayServiceStarting();

        var exchangeName = rabbitOptions.Value.OutboundExchange?.Name;
        if (!string.IsNullOrEmpty(exchangeName))
        {
            foreach (var endpoint in options.Value.Endpoints)
            {
                var uri = endpoint.Uri;
                if (uri is not null)
                {
                    try
                    {
                        var consumer = rabbitService.ConsumeMessages(
                            async message => await ProcessMessage(message, uri, exchangeName),
                            endpoint.Queue);
                        consumers.Add(consumer);
                        logger.RelayEndpointConfigured(uri, exchangeName, endpoint.Queue?.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.ErrorConfiguringRelayEndpoint(ex, uri, exchangeName, endpoint.Queue?.Name);
                    }
                }
            }

            logger.RelayServiceStarted();
        }
        else
        {
            logger.MissingOutboundExchange();
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dispose other consumers.")]
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.RelayServiceStopping();
        while (consumers.Count > 0)
        {
            var index = consumers.Count - 1;
            var consumer = consumers[index];
            consumers.RemoveAt(index);

            try
            {
                consumer.Dispose();
            }
            catch (Exception ex)
            {
                logger.ErrorClosingRelayConsumer(ex);
            }
        }

        logger.RelayServiceStopped();
        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Requeue the message and retry deliver again.")]
    private async Task<ProcessMessageResult> ProcessMessage(Message message, Uri endpoint, string exchangeName)
    {
        logger.RelayingMessage(endpoint, exchangeName, message.Properties, message.Body.Length);

        string? responseContent = null;
        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = endpoint;
            httpClient.Timeout = TimeSpan.FromMilliseconds(options.Value.Timeout);

            using var content = new StreamContent(await messageSerializer.ConvertMessageToStream(message));
            var response = await httpClient.PostAsync(new Uri("message", UriKind.Relative), content);
            responseContent = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            logger.MessageRelayed(endpoint, exchangeName, message.Properties, message.Body.Length);
            return ProcessMessageResult.Success;
        }
        catch (HttpRequestException ex) when (IsBadRequestStatusCode(ex.StatusCode))
        {
            // Reject and do not retry, when HTTP Status Code is 4xx.
            logger.ErrorRelayingMessage(ex, endpoint, exchangeName, message.Properties, message.Body.Length, responseContent);
            return ProcessMessageResult.Reject;
        }
        catch (Exception ex)
        {
            logger.ErrorRelayingMessage(ex, endpoint, exchangeName, message.Properties, message.Body.Length, responseContent);

            // Delay returning of the message back to the queue, so that retry to forward the message
            // is done after some delay. This avoids excessive usage of CPU and network.
            await Task.Delay(options.Value.RequeueDelay);
            return ProcessMessageResult.Requeue;
        }

        static bool IsBadRequestStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode.HasValue && statusCode.Value >= HttpStatusCode.BadRequest && statusCode.Value < HttpStatusCode.InternalServerError;
        }
    }
}
