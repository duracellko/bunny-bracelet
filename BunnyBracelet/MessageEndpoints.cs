using Microsoft.Extensions.Options;

namespace BunnyBracelet;

/// <summary>
/// Minimal API ASP.NET Core endpoint to receive a message from source BunnyBracelet.
/// </summary>
internal static class MessageEndpoints
{
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
    /// - 403 - Forbidden, when <see cref="RabbitOptions.InboundExchange"/> is not configured
    ///     and this endpoint is disabled.
    /// - 500 - Internal Server Error, when there was any error to putting the Message
    ///     into RabbitMQ.
    /// </remarks>
    private static async Task PostMessage(HttpContext context)
    {
        var rabbitService = context.RequestServices.GetRequiredService<RabbitService>();
        var messageSerializer = context.RequestServices.GetRequiredService<IMessageSerializer>();
        var options = context.RequestServices.GetRequiredService<IOptions<RabbitOptions>>();
        var logger = GetLogger(context);

        var message = default(Message);
        logger.ReceivingInboundMessage(context.Request.ContentLength, context.TraceIdentifier);

        if (string.IsNullOrEmpty(options.Value.InboundExchange?.Name))
        {
            logger.MissingInboundExchange();
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            message = await messageSerializer.ReadMessage(context.Request.Body, rabbitService.CreateBasicProperties);
            rabbitService.SendMessage(message);

            logger.InboundMessageForwarded(message.Properties, message.Body.Length, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (MessageException ex)
        {
            logger.ErrorReadingInboundMessage(ex, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(ex.Message);
        }
        catch (Exception ex)
        {
            logger.ErrorProcessingInboundMessage(ex, message.Properties, message.Body.Length, context.TraceIdentifier);
            throw;
        }
    }

    private static ILogger GetLogger(HttpContext context)
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        return loggerFactory.CreateLogger(Program.ApplicationName + "." + nameof(MessageEndpoints));
    }
}
