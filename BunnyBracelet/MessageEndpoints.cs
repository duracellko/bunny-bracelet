using Microsoft.Extensions.Options;

namespace BunnyBracelet;

public static class MessageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/message", PostMessage)
            .RequireRateLimiting(Program.SynchronousPolicy);
    }

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
