namespace FusionModules.Tool;

/// <summary>
/// <c>dotnet modules topology</c>: the hosts of an inventory taken together, as one host will compose
/// them, and what that host would say — before anyone builds it. Each kind of collision comes with
/// what to do about it by hand; the startup check finds the same ones once the host runs.
/// </summary>
internal static class Topology
{
    public static TopologyReport Preview(InventoryDocument inventory, IReadOnlyCollection<string> names)
    {
        var hosts = Names.Hosts(inventory, names);
        return new TopologyReport(
            [.. hosts.Select(host => host.Service)],
            [.. hosts.Where(host => host.Runtime?.Services is null).Select(host => host.Service)],
            [.. hosts.Where(host => host.Runtime?.Services is not null && host.Runtime.Modules is not { Count: > 0 }).Select(host => host.Service)],
            Collisions.Find([.. hosts.Where(host => host.Runtime?.Services is not null)]));
    }

    /// <summary>Every pair of the named hosts — of all of them when none is named — with what the pair collides on.</summary>
    public static IEnumerable<string> Pairs(InventoryDocument inventory, IReadOnlyCollection<string> names)
    {
        var hosts = Names.Hosts(inventory, names).Where(host => host.Runtime?.Services is not null).OrderBy(host => host.Service, StringComparer.Ordinal).ToList();
        for (var i = 0; i < hosts.Count; i++)
        {
            for (var j = i + 1; j < hosts.Count; j++)
            {
                var counts = TopologyReport.Counts(Collisions.Find([hosts[i], hosts[j]]));
                yield return $"{hosts[i].Service} + {hosts[j].Service}: {(counts.Count == 0 ? "nothing" : string.Join(", ", counts))}";
            }
        }
    }
}

internal sealed record TopologyReport(List<string> Hosts, List<string> WithoutRuntime, List<string> WithoutModules, CollisionsInfo Collisions)
{
    /// <summary>The composed host would not start: modules that register one service differently.</summary>
    public bool FailsStartup => Collisions.Services.Any(collision => collision.ByModules);

    /// <summary>What a set of collisions comes to, kind by kind, in the order of their consequences.</summary>
    public static List<string> Counts(CollisionsInfo collisions)
    {
        var counts = new List<string>();
        Add(collisions.Services.Count(collision => collision.ByModules), "service collision", " that fails startup", " that fail startup");
        Add(collisions.EndpointNames.Count, "endpoint name", " that fails requests", " that fail requests");
        Add(collisions.Services.Count(collision => !collision.ByModules), "registration", " to reconcile", " to reconcile");
        Add(collisions.Routes.Count(route => !route.SharedHandler), "route", " in two services", " in two services");
        Add(collisions.Routes.Count(route => route.SharedHandler), "shared route", " to map once", " to map once");
        Add(collisions.TypeNames.Count, "type name", " from two assemblies", " from two assemblies");
        Add(collisions.ConfigurationKeys.Count, "configuration key", " with two values", " with two values");
        Add(collisions.DbContexts.Count, "DbContext", " with two databases", " with two databases");
        return counts;

        void Add(int count, string what, string one, string many)
        {
            if (count > 0)
            {
                counts.Add(ComparisonResult.Count(count, what) + (count == 1 ? one : many));
            }
        }
    }

    public void Write(TextWriter writer)
    {
        writer.WriteLine($"One host composing {string.Join(", ", Hosts)}:");
        foreach (var host in WithoutRuntime)
        {
            writer.WriteLine($"  {host}: not probed — its registrations and endpoints are not in the inventory, and nothing of it is compared");
        }

        if (WithoutModules.Count > 0)
        {
            writer.WriteLine($"  no module yet: {string.Join(", ", WithoutModules)} — each registration is counted as its future module's; move Program.cs into a module (MOD0015) and take the inventory again to tell the program's apart");
        }

        writer.WriteLine();
        Section(writer, "Fails startup: modules register one service differently",
            "Keep one implementation, or register a keyed service or one contract per module. A contract consumed as IEnumerable<T> takes [MultipleImplementations] (a type you do not own: FusionModules:AllowedServiceCollisions); alternatives must not be loaded by one topology.",
            Collisions.Services.Where(collision => collision.ByModules), collision => collision.ServiceType,
            collision => collision.Sites.Select(site => $"{site.Service}{(site.Module == site.Service ? "" : $" ({site.Module})")}: {string.Join(", ", site.Implementations)}"));
        Section(writer, "Fails requests: one endpoint name, two endpoints",
            "Endpoint names are unique in a host: give each module its own (WithName, [HttpGet(Name = …)]).",
            Collisions.EndpointNames, collision => collision.Name, collision => collision.Sites.Select(site => $"{site.Service}: {site.Where}"));
        Section(writer, "To reconcile: the programs register one service differently",
            "The composed host registers it once, from its own program: choose the registration it keeps, and move what is a service's own into that service's module.",
            Collisions.Services.Where(collision => !collision.ByModules), collision => collision.ServiceType,
            collision => collision.Sites.Select(site => $"{site.Service}: {string.Join(", ", site.Implementations)}"));
        Section(writer, "Logged at startup: one route in two services",
            "A request it matches gets a 500 (AmbiguousMatchException): move one under a prefix (MapGroup, a controller route), or tell them apart by method or host.",
            Collisions.Routes.Where(route => !route.SharedHandler), route => route.Route,
            route => route.Sites.Select(site => $"{site.Service}: {string.Join(",", site.Methods ?? ["*"])} {site.Route} ({site.Handler})"));
        Section(writer, "One shared handler in several services",
            "Map it once, in the host, and remove it from the modules.",
            Collisions.Routes.Where(route => route.SharedHandler), route => route.Route,
            route => [string.Join(", ", route.Sites.Select(site => site.Service).Distinct(StringComparer.Ordinal))]);
        Section(writer, "One type name, two assemblies",
            "Named HTTP and gRPC clients and options are keyed by the type's name and see one: share one type from a library, or rename one.",
            Collisions.TypeNames, collision => collision.Name, collision => collision.Sites.Select(site => $"{site.Service}: {site.Where}"));
        Section(writer, "One configuration key, two values",
            "The host has one configuration: give the key one value, or move each module's under a section of its own and read that.",
            Collisions.ConfigurationKeys, key => $"{key.Key} ({key.Source})", key => key.Values.Select(site => $"{site.Service}: {site.Value ?? "(null)"}"));
        Section(writer, "One DbContext, two databases",
            "In one host one of them writes to the other's database: give each its own connection string name, or move them into one model (FusionModules.EntityFrameworkCore).",
            Collisions.DbContexts, collision => collision.Type,
            collision => collision.Sites.Select(site => $"{site.Service}: {site.Provider ?? "?"}: {site.ConnectionString ?? "(no connection string)"}"));

        var counts = Counts(Collisions);
        writer.WriteLine(counts.Count == 0 ? "Nothing collides." : string.Join(", ", counts) + ".");
    }

    private static void Section<T>(TextWriter writer, string title, string advice, IEnumerable<T> items, Func<T, string> name, Func<T, IEnumerable<string>> sites)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        writer.WriteLine($"{title} ({list.Count}):");
        foreach (var item in list)
        {
            writer.WriteLine($"  {name(item)}");
            foreach (var site in sites(item))
            {
                writer.WriteLine($"    {site}");
            }
        }

        writer.WriteLine($"  What to do: {advice}");
        writer.WriteLine();
    }
}
