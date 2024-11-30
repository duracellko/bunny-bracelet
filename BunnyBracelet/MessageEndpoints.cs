using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

using ByteArray = byte[];

namespace BunnyBracelet;

/// <summary>
/// Minimal API ASP.NET Core endpoint to receive a message from source BunnyBracelet.
/// </summary>
internal static class MessageEndpoints
{
    public const string KeyIndexHeaderName = Program.ApplicationName + "-AuthenticationKeyIndex";

    public static void Map(WebApplication app)
    {
        // The endpoint is configured to handle single request at a time.
        // The endpoint sends messages to RabbitMQ. However, only single channel
        // is used and the channel is not thread-safe.
        app.MapPost("/message", PostMessage)
            .RequireRateLimiting(Program.SynchronousPolicy);
    }

    /// <summary>
    /// Loads a <see cref="Message"/> from HTTP request body and puts it
    /// into configured RabbitMQ exchange.
    /// </summary>
    /// <remarks>
    /// Output of this endpoint is one of the following HTTP status codes:
    /// - 204 - No content, when the Message was successfully put into RabbitMQ.
    /// - 400 - Bad request, when input has incorrect format and it was not possible
    ///     to deserialize Message from the input.
    /// - 401 - Unauthorized, when message authentication code is incorrect or missing.
    /// - 403 - Forbidden, when <see cref="RabbitOptions.InboundExchange"/> is not configured
    ///     and this endpoint is disabled.
    /// - 500 - Internal Server Error, when there was any error to putting the Message
    ///     into RabbitMQ.
    /// </remarks>
    private static async Task PostMessage(HttpContext context)
    {
        var rabbitService = context.RequestServices.GetRequiredService<RabbitService>();
        var messageSerializer = context.RequestServices.GetRequiredService<IMessageSerializer>();
        var options = context.RequestServices.GetRequiredService<IOptions<RelayOptions>>();
        var rabbitOptions = context.RequestServices.GetRequiredService<IOptions<RabbitOptions>>();
        var logger = GetLogger(context);

        Message? message = default;
        logger.ReceivingInboundMessage(context.Request.ContentLength, context.TraceIdentifier);

        if (string.IsNullOrEmpty(rabbitOptions.Value.InboundExchange?.Name))
        {
            logger.MissingInboundExchange();
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var cancellationToken = context.RequestAborted;

        try
        {
            var authenticationOptions = options.Value.Authentication;
            if (RequiresAuthentication(authenticationOptions))
            {
                message = await ReadAndAuthenticateMessage(context, messageSerializer, authenticationOptions, logger, cancellationToken);
            }
            else
            {
                message = await messageSerializer.ReadMessage(context.Request.Body, cancellationToken);
            }

            if (message.HasValue && CheckMessageTimestamp(message.Value, context, options, logger))
            {
                await rabbitService.SendMessage(message.Value, cancellationToken);

                logger.InboundMessageForwarded(message.Value.Properties, message.Value.Body.Length, context.TraceIdentifier);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        }
        catch (MessageException ex)
        {
            logger.ErrorReadingInboundMessage(ex, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.ErrorProcessingInboundMessage(ex, message?.Properties, message?.Body.Length, context.TraceIdentifier);
            throw;
        }
    }

    private static async ValueTask<Message?> ReadAndAuthenticateMessage(
        HttpContext context,
        IMessageSerializer messageSerializer,
        RelayAuthenticationOptions authenticationOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var keyIndex = GetAuthenticationKeyIndex(context);
        if (!keyIndex.HasValue)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            logger.ErrorMissingAuthenticationKeyIndex();
            return null;
        }

        var key = GetAuthenticationKey(keyIndex.Value, authenticationOptions);
        if (key is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            logger.ErrorMissingAuthenticationKey(keyIndex.Value);
            return null;
        }

        using var hmac = new HMACSHA256(key);
        using var extendedTailStream = new ExtendedTailStream(context.Request.Body, hmac.HashSize / 8);
        using var stream = new CryptoStream(extendedTailStream, hmac, CryptoStreamMode.Read);

        try
        {
            var message = await messageSerializer.ReadMessage(stream, cancellationToken);
            if (CheckAuthenticationCode(hmac, extendedTailStream.Tail))
            {
                logger.MessageAuthenticated(keyIndex.Value);
                return message;
            }
        }
        catch (MessageException)
        {
            await stream.ReadToEndAsync(cancellationToken);
            if (CheckAuthenticationCode(hmac, extendedTailStream.Tail))
            {
                logger.MessageAuthenticated(keyIndex.Value);
                throw;
            }
        }

        logger.ErrorMessageAuthentication(keyIndex.Value);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return null;
    }

    /// <summary>
    /// Rejects too old messages to mitigate replay attack.
    /// </summary>
    private static bool CheckMessageTimestamp(
        Message message,
        HttpContext context,
        IOptions<RelayOptions> options,
        ILogger logger)
    {
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();

        var timeout = TimeSpan.FromMilliseconds(options.Value.Timeout);
        if (message.Timestamp < timeProvider.GetUtcNow().UtcDateTime.Subtract(timeout))
        {
            logger.ErrorMessageTimestampAuthentication(message.Timestamp);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        return true;
    }

    private static bool RequiresAuthentication([NotNullWhen(true)] RelayAuthenticationOptions? authenticationOptions)
    {
        return authenticationOptions is not null &&
            (authenticationOptions.Key1 is not null || authenticationOptions.Key2 is not null);
    }

    private static int? GetAuthenticationKeyIndex(HttpContext context)
    {
        var useKeyIndexHeader = context.Request.Headers[KeyIndexHeaderName];
        if (int.TryParse(useKeyIndexHeader.ToString(), CultureInfo.InvariantCulture, out var keyIndex))
        {
            return keyIndex >= 0 ? keyIndex : null;
        }

        return null;
    }

    private static ByteArray? GetAuthenticationKey(int keyIndex, RelayAuthenticationOptions authenticationOptions)
    {
        return keyIndex switch
        {
            1 => authenticationOptions.Key1,
            2 => authenticationOptions.Key2,
            _ => null
        };
    }

    private static bool CheckAuthenticationCode(HMAC hmac, ByteArray authenticationCode)
    {
        var hmacCode = hmac.Hash;
        return hmacCode is not null && hmacCode.AsSpan().SequenceEqual(authenticationCode);
    }

    private static ILogger GetLogger(HttpContext context)
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        return loggerFactory.CreateLogger(Program.ApplicationName + "." + nameof(MessageEndpoints));
    }
}
