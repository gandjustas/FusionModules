using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// What hosts that were apart collide on once they compose one process, from what they actually
/// register and map rather than from a reading of the source. The inventory's <c>collisions</c> are
/// these over all its hosts; <c>dotnet modules topology</c> takes them over the hosts it names, which
/// is what that host would say before anyone builds it.
/// </summary>
internal static class Collisions
{
    private const string FusionModulesAssembly = "FusionModules";

    public static CollisionsInfo Find(IReadOnlyList<HostInfo> hosts) => new()
    {
        Services = Services(hosts),
        Routes = Routes(hosts),
        EndpointNames = EndpointNames(hosts),
        TypeNames = TypeNames(hosts),
        ConfigurationKeys = ConfigurationKeys(hosts),
        DbContexts = DbContexts(hosts),
    };

    /// <summary>
    /// The startup check's rule over the inventory: a service type that two owners register with
    /// different sets of implementations — the same implementation twice is redundant, not wrong.
    /// Implementations are told apart with their assemblies, as the check tells types apart.
    /// </summary>
    /// <remarks>
    /// The check is on what modules register, each module an owner, in one host or in two. What a
    /// program registers, the composed host registers once, from one program: two programs that
    /// disagree are a reconciliation, listed apart. A host with no module yet counts as its own future
    /// module. The framework's services, consumed as sequences, and those shared by design are left out.
    /// </remarks>
    private static List<ServiceCollision> Services(IReadOnlyList<HostInfo> hosts)
    {
        var registrations = hosts
            .SelectMany(host => (host.Runtime?.Services ?? []).Where(Counts).Select(service => (
                Host: host.Service,
                Module: service.Module ?? (host.Runtime!.Modules is { Count: > 0 } ? null : host.Service),
                Service: service)))
            .GroupBy(entry => Names.Service(entry.Service), StringComparer.Ordinal);

        var collisions = new List<ServiceCollision>();
        foreach (var group in registrations)
        {
            foreach (var byModules in new[] { true, false })
            {
                var owners = group
                    .Where(entry => entry.Module is not null == byModules)
                    .GroupBy(entry => (entry.Host, entry.Module))
                    .Select(owner => new ServiceSite
                    {
                        Service = owner.Key.Host,
                        Module = owner.Key.Module,
                        Implementations = [.. owner.Select(entry => Names.Implementation(entry.Service)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    })
                    .ToList();
                if (owners.Count > 1 && owners.Any(owner => !owner.Implementations.SequenceEqual(owners[0].Implementations)))
                {
                    collisions.Add(new ServiceCollision { ServiceType = group.Key, ByModules = byModules, Sites = owners });
                }
            }
        }

        return [.. collisions.OrderBy(collision => !collision.ByModules).ThenBy(collision => collision.ServiceType, StringComparer.Ordinal)];
    }

    private static bool Counts(ProbeService service) =>
        !TopologyConventions.IsFrameworkAssembly(service.ServiceAssembly) &&
        !TopologyConventions.SharedByDesign.Contains(service.ServiceType) &&
        service.Assembly != FusionModulesAssembly &&
        service.ServiceAssembly != FusionModulesAssembly;

    /// <summary>
    /// One route in two services with overlapping methods (an endpoint without methods overlaps every
    /// method): the startup check logs it, and a request it matches gets a 500.
    /// </summary>
    private static List<RouteCollision> Routes(IReadOnlyList<HostInfo> hosts)
    {
        var collisions = new List<RouteCollision>();
        var sites = hosts
            .SelectMany(host => (host.Runtime?.Endpoints ?? []).Where(endpoint => endpoint.Route is not null).Select(endpoint => (Host: host, Endpoint: endpoint)))
            .GroupBy(site => Names.Route(site.Endpoint.Route!), StringComparer.Ordinal);
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
                SharedHandler = overlapping.Select(site => site.Endpoint.Handler).Distinct().Count() == 1,
                Sites = [.. overlapping.Select(site => new RouteSite
                {
                    Service = site.Host.Service,
                    Route = site.Endpoint.Route,
                    Methods = site.Endpoint.Methods,
                    Handler = site.Endpoint.Handler,
                    Assembly = site.Endpoint.Assembly,
                    Authorization = site.Endpoint.Authorization,
                })],
            });
        }

        return [.. collisions.OrderBy(collision => collision.Route, StringComparer.Ordinal)];
    }

    private static bool Overlap(List<string>? a, List<string>? b) =>
        a is null || b is null || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>
    /// One endpoint name on two endpoints: names are unique in a host, and generating a link by name
    /// throws on the first request that resolves them — every request, with MVC.
    /// </summary>
    private static List<NameCollision> EndpointNames(IReadOnlyList<HostInfo> hosts) =>
        Named(hosts.SelectMany(host => (host.Runtime?.Endpoints ?? [])
            .Where(endpoint => endpoint.Name is not null)
            .Select(endpoint => (endpoint.Name!, host.Service, $"{string.Join(",", endpoint.Methods ?? ["*"])} {endpoint.Route}"))),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One full type name from two assemblies — a generated client compiled into each service, say:
    /// two types to the container, one name to whatever keys by name, named HTTP and gRPC clients and
    /// options among them.
    /// </summary>
    private static List<NameCollision> TypeNames(IReadOnlyList<HostInfo> hosts) =>
        Named(hosts.SelectMany(host => (host.Runtime?.Services ?? [])
            .Where(service => service.ServiceAssembly is not null && !TopologyConventions.IsFrameworkAssembly(service.ServiceAssembly))
            .Select(service => (service.ServiceType, host.Service, service.ServiceAssembly!))
            .Distinct()),
            StringComparer.Ordinal,
            twoPlaces: sites => sites.Select(site => site.Where).Distinct(StringComparer.Ordinal).Count() > 1);

    private static List<NameCollision> Named(IEnumerable<(string Name, string Service, string Where)> entries, StringComparer comparer, Func<List<NameSite>, bool>? twoPlaces = null) =>
        [.. entries
            .GroupBy(entry => entry.Name, comparer)
            .Select(group => new NameCollision { Name = group.Key, Sites = [.. group.Select(entry => new NameSite { Service = entry.Service, Where = entry.Where })] })
            .Where(collision => collision.Sites.Count > 1 &&
                                (twoPlaces is null || (twoPlaces(collision.Sites) && collision.Sites.Select(site => site.Service).Distinct().Count() > 1)))
            .OrderBy(collision => collision.Name, StringComparer.Ordinal)];

    /// <summary>
    /// One configuration key with two values, per layer: each appsettings file, and what the
    /// deployment sets — minus what the tool made up and the runtime's own switches. The host has one
    /// configuration, and one of the services reads the other's value.
    /// </summary>
    private static List<ConfigurationCollision> ConfigurationKeys(IReadOnlyList<HostInfo> hosts)
    {
        var entries = new List<(string Source, string Key, string Service, string? Value)>();
        foreach (var host in hosts)
        {
            foreach (var (file, keys) in host.Configuration ?? [])
            {
                entries.AddRange(keys.Select(key => (file.ToLowerInvariant(), key.Key, host.Service, key.Value)));
            }

            var faked = host.FakedEnvironment?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var (name, value) in host.Environment ?? [])
            {
                if (!faked.Contains(name) && !RuntimeSwitch(name))
                {
                    entries.Add(("environment", AppSettingsReader.KeyOf(name), host.Service, value));
                }
            }
        }

        return [.. entries
            .GroupBy(entry => (entry.Source, Key: entry.Key.ToLowerInvariant()))
            .Where(group => group.Select(entry => entry.Service).Distinct().Count() > 1 && group.Select(entry => entry.Value).Distinct().Count() > 1)
            .Select(group => new ConfigurationCollision
            {
                Key = group.First().Key,
                Source = group.Key.Source,
                Values = [.. group.Select(entry => new ConfigurationSite { Service = entry.Service, Value = entry.Value })],
            })
            .OrderBy(collision => collision.Source, StringComparer.Ordinal)
            .ThenBy(collision => collision.Key, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool RuntimeSwitch(string name) =>
        name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase);

    /// <summary>One context type that two services point at different databases or providers: in one host, one of them writes to the other's.</summary>
    private static List<DbContextCollision> DbContexts(IReadOnlyList<HostInfo> hosts) =>
        [.. hosts
            .SelectMany(host => (host.Runtime?.DbContexts ?? []).Select(context => (host.Service, Context: context)))
            .GroupBy(entry => entry.Context.Type, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Service).Distinct().Count() > 1 &&
                            group.Select(entry => (entry.Context.Provider, entry.Context.ConnectionString)).Distinct().Count() > 1)
            .Select(group => new DbContextCollision
            {
                Type = group.Key,
                Sites = [.. group.Select(entry => new DbContextSite { Service = entry.Service, Provider = entry.Context.Provider, ConnectionString = entry.Context.ConnectionString })],
            })
            .OrderBy(collision => collision.Type, StringComparer.Ordinal)];
}
