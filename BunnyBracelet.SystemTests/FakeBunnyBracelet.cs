using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace BunnyBracelet.SystemTests;

/// <summary>
/// Fake BunnyBracelet web server that simulates '/message' endpoint
/// that does not respond in 30 seconds.
/// </summary>
internal sealed class FakeBunnyBracelet : IAsyncDisposable
{
    private WebApplication? application;
    private Uri? uri;

    public int Port { get; }

    public Uri? Uri
    {
        get
        {
            if (application is null)
            {
                return null;
            }

            if (uri is null)
            {
                var server = (IServer)application.Services.GetRequiredService(typeof(IServer));
                var serverAddressesFeature = server.Features.Get<IServerAddressesFeature>();
                if (serverAddressesFeature != null)
                {
                    var address = serverAddressesFeature.Addresses.Single();
                    uri = new Uri(address);
                }
            }

            return uri;
        }
    }

    public async Task Start()
    {
        if (application is not null)
        {
            throw new InvalidOperationException("Fake BunnyBracelet is already running.");
        }

        var app = CreateApplicationBuilder().Build();
        app.MapPost("/message", PostMessage);

        application = app;
        await app.StartAsync();
    }

    public async Task Stop()
    {
        if (application is not null)
        {
            await application.StopAsync();
            await application.DisposeAsync();
            application = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
    }

    private static WebApplicationBuilder CreateApplicationBuilder()
    {
        var arguments = new[] { "--urls", "http://127.0.0.1:0" };
        var builder = WebApplication.CreateSlimBuilder(arguments);
        return builder;
    }

    private async Task<IResult> PostMessage(CancellationToken cancellationToken)
    {
        await Task.Delay(30000, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Results.NoContent();
    }
}
