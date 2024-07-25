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

        var message = await messageSerializer.ReadMessage(context.Request.Body, rabbitService.CreateBasicProperties);
        rabbitService.SendMessage(message);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
