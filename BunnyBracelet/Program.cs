using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;

namespace BunnyBracelet;

/// <summary>
/// Main entry-point to BunnyBracelet program.
/// </summary>
public static partial class Program
{
    public const string ApplicationName = "BunnyBracelet";

    internal static string SynchronousPolicy => "Synchronous";

    public static void Main(string[] args)
    {
        if (OutputVersion(args))
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder(args);
        var applicationConfigurationSection = builder.Configuration.GetSection(ApplicationName);

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<RabbitOptions>(applicationConfigurationSection);
        builder.Services.Configure<RelayOptions>(applicationConfigurationSection);
        builder.Services.AddSingleton<RabbitService>();
        builder.Services.AddSingleton<IMessageSerializer, MessageSerializer>();
        builder.Services.AddSingleton<RelayHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayHostedService>());

        builder.Services.AddRateLimiter(
            o => o.AddConcurrencyLimiter(
                SynchronousPolicy,
                options =>
                {
                    options.PermitLimit = 1;
                    options.QueueLimit = 8;
                }));

        builder.Services.AddHealthChecks()
            .AddCheck<RabbitHealthCheck>("RabbitMQ")
            .AddCheck<RelayHealthCheck>(ApplicationName + "Relay");

        var app = builder.Build();

        MessageEndpoints.Map(app);
        app.MapHealthChecks("/health");

        app.Run();
    }

    private static bool OutputVersion(string[] args)
    {
        var hasVersionArg = args.Contains("--version");

        if (hasVersionArg)
        {
            var assembly = typeof(Program).Assembly;
            var attributes = assembly.GetCustomAttributes(typeof(AssemblyProductAttribute), false);
            var productAttribute = (AssemblyProductAttribute?)attributes.FirstOrDefault();

            Console.WriteLine("{0} {1}", productAttribute?.Product, assembly.GetName().Version);
            Console.WriteLine("Licensed under Apache License 2.0");
        }

        return hasVersionArg;
    }
}
