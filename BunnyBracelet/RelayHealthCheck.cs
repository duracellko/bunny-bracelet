using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BunnyBracelet;

public class RelayHealthCheck : IHealthCheck
{
    private const string RelayDisabledDescription = Program.ApplicationName + " relay is disabled by configuration.";
    private const string HealthyDescription = Program.ApplicationName + " relay consumers are healthy.";
    private const string UnhealthyDescription = Program.ApplicationName + " relay consumers are failed.";
    private const string DegradedDescription = "Some " + Program.ApplicationName + " relay consumers are failed.";

    private readonly RelayHostedService relayHostedService;
    private readonly IOptions<RelayOptions> options;
    private readonly IOptions<RabbitOptions> rabbitOptions;

    public RelayHealthCheck(
        RelayHostedService relayHostedService,
        IOptions<RelayOptions> options,
        IOptions<RabbitOptions> rabbitOptions)
    {
        ArgumentNullException.ThrowIfNull(relayHostedService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rabbitOptions);

        this.relayHostedService = relayHostedService;
        this.options = options;
        this.rabbitOptions = rabbitOptions;
    }

    private int ValidEndpointsCount => options.Value.Endpoints.Count(e => e.Uri is not null);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CheckHealth());
    }

    private HealthCheckResult CheckHealth()
    {
        if (string.IsNullOrEmpty(rabbitOptions.Value.OutboundExchange?.Name))
        {
            return HealthCheckResult.Healthy(RelayDisabledDescription);
        }

        var consumersCount = relayHostedService.ConsumersCount;

        if (consumersCount == ValidEndpointsCount)
        {
            return HealthCheckResult.Healthy(HealthyDescription);
        }
        else if (consumersCount == 0)
        {
            return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }
        else
        {
            return HealthCheckResult.Degraded(DegradedDescription);
        }
    }
}
