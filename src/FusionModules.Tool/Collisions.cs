using System.Text.RegularExpressions;

namespace FusionModules.Tool;

/// <summary>
/// The three silent collisions of a merge, from what the hosts actually compose rather than from a
/// reading of the source: the same route in two services, the same configuration key with two
/// values, the same application service type with two implementations. All three are decided by
/// load order once the services share a process, and nothing reports them.
/// </summary>
internal static partial class Collisions
{
    public static CollisionsInfo Find(IReadOnlyList<HostInfo> hosts) => new()
    {
        Routes = Routes(hosts),
        ConfigurationKeys = ConfigurationKeys(hosts),
        Services = Services(hosts),
    };

    /// <summary>Route parameters compared by position only: {id:int} and {itemId} are the same route.</summary>
    internal static string NormalizeRoute(string route)
    {
        var normalized = Parameter().Replace(route.Trim().ToLowerInvariant(), "{}");
        return "/" + normalized.Trim('/');
    }

    private static List<RouteCollision> Routes(IReadOnlyList<HostInfo> hosts)
    {
        var sites = hosts
            .SelectMany(h => (h.Runtime?.Endpoints ?? []).Where(e => e.Route is not null).Select(e => (Host: h, Endpoint: e)))
            .GroupBy(s => NormalizeRoute(s.Endpoint.Route!), StringComparer.Ordinal);

        var collisions = new List<RouteCollision>();
        foreach (var group in sites)
        {
            var entries = group.ToList();
            var overlapping = entries
                .Where(a => entries.Any(b => b.Host.Service != a.Host.Service && Overlap(a.Endpoint.Methods, b.Endpoint.Methods)))
                .ToList();
            if (overlapping.Count == 0)
            {
                continue;
            }

            collisions.Add(new RouteCollision
            {
                Route = group.Key,
                SharedHandler = overlapping.Select(s => s.Endpoint.Handler).Distinct().Count() == 1,
                Sites = [.. overlapping.Select(s => new RouteSite
                {
                    Service = s.Host.Service,
                    Route = s.Endpoint.Route,
                    Methods = s.Endpoint.Methods,
                    Handler = s.Endpoint.Handler,
                    Assembly = s.Endpoint.Assembly,
                    Authorization = s.Endpoint.Authorization,
                })],
            });
        }

        return [.. collisions.OrderBy(c => c.Route, StringComparer.Ordinal)];
    }

    private static bool Overlap(List<string>? a, List<string>? b) =>
        a is null || b is null || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();

    private static List<ConfigurationCollision> ConfigurationKeys(IReadOnlyList<HostInfo> hosts)
    {
        var entries = new List<(string Source, string Key, string Service, string? Value)>();
        foreach (var host in hosts)
        {
            foreach (var (file, keys) in host.Configuration ?? [])
            {
                entries.AddRange(keys.Select(k => (file.ToLowerInvariant(), k.Key, host.Service, k.Value)));
            }

            // What the deployment sets, minus what the tool made up and the runtime's own switches.
            var faked = host.FakedEnvironment?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var (name, value) in host.Environment ?? [])
            {
                if (faked.Contains(name) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(("environment", AppSettingsReader.KeyOf(name), host.Service, value));
            }
        }

        return [.. entries
            .GroupBy(e => (e.Source, Key: e.Key.ToLowerInvariant()))
            .Where(g => g.Select(e => e.Service).Distinct().Count() > 1 && g.Select(e => e.Value).Distinct().Count() > 1)
            .Select(g => new ConfigurationCollision
            {
                Key = g.First().Key,
                Source = g.Key.Source,
                Values = [.. g.Select(e => new ConfigurationSite { Service = e.Service, Value = e.Value })],
            })
            .OrderBy(c => c.Source, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<ServiceCollision> Services(IReadOnlyList<HostInfo> hosts)
    {
        var entries = new List<(string ServiceType, string Service, Probe.ProbeService Registration)>();
        foreach (var host in hosts)
        {
            var application = host.Runtime?.ApplicationAssemblies?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var registration in host.Runtime?.Services ?? [])
            {
                // An application-defined service type; framework interfaces (IConfigureOptions<>,
                // IHostedService) are meant to be registered many times.
                if (registration.ServiceAssembly is { } assembly && application.Contains(assembly))
                {
                    entries.Add((registration.Key is null ? registration.ServiceType : $"{registration.ServiceType} [{registration.Key}]", host.Service, registration));
                }
            }
        }

        return [.. entries
            .GroupBy(e => e.ServiceType, StringComparer.Ordinal)
            .Where(g => g.Select(e => e.Service).Distinct().Count() > 1
                        && g.Select(e => e.Registration.ImplementationType ?? e.Registration.Factory).Distinct().Count() > 1)
            .Select(g => new ServiceCollision
            {
                ServiceType = g.Key,
                Sites = [.. g.Select(e => new ServiceSite
                {
                    Service = e.Service,
                    ImplementationType = e.Registration.ImplementationType,
                    Factory = e.Registration.Factory,
                    Assembly = e.Registration.Assembly,
                    Lifetime = e.Registration.Lifetime,
                })],
            })
            .OrderBy(c => c.ServiceType, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Parameter();
}
