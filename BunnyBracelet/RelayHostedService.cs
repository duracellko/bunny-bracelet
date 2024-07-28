﻿using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Options;

namespace BunnyBracelet;

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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "On error continue setup of other endpoints.")]
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.RelayServiceStarting();

        var exchangeName = rabbitOptions.Value.OutboundExchange;
        if (!string.IsNullOrEmpty(exchangeName))
        {
            foreach (var endpoint in options.Value.Endpoints)
            {
                try
                {
                    var consumer = rabbitService.ConsumeMessages(async m => await ProcessMessage(m, endpoint, exchangeName));
                    consumers.Add(consumer);
                    logger.RelayEndpointConfigured(endpoint, exchangeName);
                }
                catch (Exception ex)
                {
                    logger.ErrorConfiguringRelayEndpoint(ex, endpoint, exchangeName);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.RelayServiceStopping();
        while (consumers.Count > 0)
        {
            var index = consumers.Count - 1;
            consumers[index].Dispose();
            consumers.RemoveAt(index);
        }

        logger.RelayServiceStopped();
        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Requeue the message and retry deliver again.")]
    private async Task<ProcessMessageResult> ProcessMessage(Message message, Uri endpoint, string exchangeName)
    {
        logger.RelayingMessage(endpoint, exchangeName, message.Properties, message.Body.Length);

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = endpoint;

            using var content = new StreamContent(await messageSerializer.ConvertMessageToStream(message));
            await httpClient.PostAsync(new Uri("message", UriKind.Relative), content);

            logger.MessageRelayed(endpoint, exchangeName, message.Properties, message.Body.Length);
            return ProcessMessageResult.Success;
        }
        catch (HttpRequestException ex) when (IsBadRequestStatusCode(ex.StatusCode))
        {
            // Reject and do not retry, when HTTP Status Code is 4xx.
            logger.ErrorRelayingMessage(ex, endpoint, exchangeName, message.Properties, message.Body.Length);
            return ProcessMessageResult.Reject;
        }
        catch (Exception ex)
        {
            logger.ErrorRelayingMessage(ex, endpoint, exchangeName, message.Properties, message.Body.Length);
            return ProcessMessageResult.Requeue;
        }

        bool IsBadRequestStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode.HasValue && statusCode.Value >= HttpStatusCode.BadRequest && statusCode.Value < HttpStatusCode.InternalServerError;
        }
    }
}
