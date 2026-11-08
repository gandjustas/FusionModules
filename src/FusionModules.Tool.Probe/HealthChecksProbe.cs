using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FusionModules.Tool.Probe;

/// <summary>
/// Touches Microsoft.Extensions.Diagnostics.HealthChecks, so only reached once the application has
/// shown it registers HealthCheckService: a worker without health checks never loads the assembly.
/// </summary>
internal static class HealthChecksProbe
{
    public static void AddNoop(IServiceCollection services) => services.AddSingleton<HealthCheckService, NoopHealthCheckService>();

    /// <summary>Healthy, checking nothing: the real one would build every check's dependencies on first use.</summary>
    private sealed class NoopHealthCheckService : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero));
    }
}
