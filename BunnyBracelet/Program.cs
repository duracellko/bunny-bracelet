using Microsoft.AspNetCore.RateLimiting;

namespace BunnyBracelet;

public static partial class Program
{
    public const string ApplicationName = "BunnyBracelet";

    internal static string SynchronousPolicy => "Synchronous";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var applicationConfigurationSection = builder.Configuration.GetSection(ApplicationName);

        builder.Services.AddHttpClient();
        builder.Services.Configure<RabbitOptions>(applicationConfigurationSection);
        builder.Services.Configure<RelayOptions>(applicationConfigurationSection);
        builder.Services.AddSingleton<RabbitService>();
        builder.Services.AddSingleton<IMessageSerializer, MessageSerializer>();
        builder.Services.AddHostedService<RelayHostedService>();

        builder.Services.AddRateLimiter(
            o => o.AddConcurrencyLimiter(
                SynchronousPolicy,
                options =>
                {
                    options.PermitLimit = 1;
                    options.QueueLimit = 8;
                }));

        var app = builder.Build();

        MessageEndpoints.Map(app);

        app.Run();
    }
}
