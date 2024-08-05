using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BunnyBracelet;

/// <summary>
/// Health check that reports status of RabbitMQ connection.
/// </summary>
public class RabbitHealthCheck : IHealthCheck
{
    private const string HealthyDescription = "RabbitMQ connection is healthy.";
    private const string UnhealthyDescription = "RabbitMQ is disconnected.";

    private readonly RabbitService rabbitService;

    public RabbitHealthCheck(RabbitService rabbitService)
    {
        ArgumentNullException.ThrowIfNull(rabbitService);
        this.rabbitService = rabbitService;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var healthResult = rabbitService.IsConnected ? HealthCheckResult.Healthy(HealthyDescription) : HealthCheckResult.Unhealthy(UnhealthyDescription);
        return Task.FromResult(healthResult);
    }
}
