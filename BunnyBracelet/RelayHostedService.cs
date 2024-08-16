using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

using ByteArray = byte[];

namespace BunnyBracelet;

/// <summary>
/// ASP.NET Core application background service that consumes
/// <see cref="Message"/> objects from configured
/// <see cref="RabbitOptions.OutboundExchange"/> and forwards them
/// to configured BunnyBracelet endpoints.
/// </summary>
public class RelayHostedService : IHostedService
{
    private static readonly Uri MessageUri = new Uri("message", UriKind.Relative);

    private readonly RabbitService rabbitService;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IMessageSerializer messageSerializer;
    private readonly TimeProvider timeProvider;
    private readonly IOptions<RelayOptions> options;
    private readonly IOptions<RabbitOptions> rabbitOptions;
    private readonly ILogger<RelayHostedService> logger;
    private readonly List<IDisposable> consumers = new List<IDisposable>();

    public RelayHostedService(
        RabbitService rabbitService,
        IHttpClientFactory httpClientFactory,
        IMessageSerializer messageSerializer,
        TimeProvider timeProvider,
        IOptions<RelayOptions> options,
        IOptions<RabbitOptions> rabbitOptions,
        ILogger<RelayHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(rabbitService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(messageSerializer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rabbitOptions);
        ArgumentNullException.ThrowIfNull(logger);

        this.rabbitService = rabbitService;
        this.httpClientFactory = httpClientFactory;
        this.messageSerializer = messageSerializer;
        this.timeProvider = timeProvider;
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

    private static ByteArray? GetAuthenticationKey(RelayAuthenticationOptions authenticationOptions, int keyIndex)
    {
        var key = keyIndex switch
        {
            1 => authenticationOptions.Key1,
            2 => authenticationOptions.Key2,
            _ => null
        };

        return key is not null && key.Length > 0 ? key : null;
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

            // Update message timestamp used for authentication.
            message = new Message(message.Body, message.Properties, timeProvider.GetUtcNow().UtcDateTime);

            using var request = new HttpRequestMessage(HttpMethod.Post, MessageUri);
            using var messageStream = await messageSerializer.ConvertMessageToStream(message);

            HMACSHA256? hmac = default;
            Stream? cryptoStream = default;
            Stream? extendedTailStream = default;
            try
            {
                var requestStream = messageStream;

                var authenticationOptions = options.Value.Authentication;
                if (authenticationOptions is not null)
                {
                    var keyIndex = authenticationOptions.UseKeyIndex;
                    var key = GetAuthenticationKey(authenticationOptions, keyIndex);
                    if (key is not null)
                    {
                        // Append HMAC message authentication code at the end of the stream.
                        hmac = new HMACSHA256(key);
                        cryptoStream = new CryptoStream(messageStream, hmac, CryptoStreamMode.Read);
                        extendedTailStream = new ExtendedTailStream(cryptoStream, 0, () => GenerateAuthenticationCode(hmac, keyIndex));
                        requestStream = extendedTailStream;

                        request.Headers.Add(MessageEndpoints.KeyIndexHeaderName, keyIndex.ToString(CultureInfo.InvariantCulture));
                    }
                }

                using var content = new StreamContent(requestStream);
                request.Content = content;

                var response = await httpClient.SendAsync(request);
                responseContent = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();
            }
            finally
            {
                if (extendedTailStream != null)
                {
                    await extendedTailStream.DisposeAsync();
                }

                if (cryptoStream != null)
                {
                    await cryptoStream.DisposeAsync();
                }

                hmac?.Dispose();
            }

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

    private ReadOnlyMemory<byte> GenerateAuthenticationCode(HashAlgorithm hash, int keyIndex)
    {
        if (hash.Hash is not null)
        {
            logger.MessageAuthenticationCodeGenerated(keyIndex);
        }

        return hash.Hash;
    }
}
