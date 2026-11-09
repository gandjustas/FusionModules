using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// Two inventories compared host by host: the check that a step of a migration changed nothing a
/// host composes. What moving code into a module changes by design is not a difference — handler and
/// factory names that move from <c>Program</c> to <c>Module</c>, the compiler's lambda numbering,
/// FusionModules' own registrations — and everything else is.
/// </summary>
internal static class Comparison
{
    private const string FusionModulesAssembly = "FusionModules";

    /// <summary>
    /// What is compared: one line per thing a host composes, the same line for the same thing in both.
    /// <paramref name="Counted"/>: in a merge, how many times matters — an endpoint mapped twice fails,
    /// a bus started by five modules runs five times — where registrations add up legitimately and
    /// are compared by presence. <paramref name="Merged"/>: compared in a merge at all.
    /// </summary>
    private sealed record Section(string Name, Func<ProbeOutput, IEnumerable<string>> Lines, bool GoneIsNote = false, bool AddedIsNote = false, bool Counted = true, bool Merged = true);

    private static readonly Section[] Sections =
    [
        new("status", runtime => [runtime.Status]),
        new("environment", runtime => [runtime.EnvironmentName ?? "(none)"]),
        new("module", runtime => (runtime.Modules ?? []).Select(module => module.Type), GoneIsNote: true, AddedIsNote: true, Merged: false),
        // By what a request sees, its authorization included. The handler is left out: a lambda
        // mapped in Program.cs is Program.<Main>$… before and Module.<Configure>… after.
        new("endpoint", runtime => (runtime.Endpoints ?? []).Select(Endpoint)),
        new("static asset endpoints", runtime => [$"{runtime.StaticAssetEndpoints ?? 0}"], Merged: false),
        new("registration", runtime => Registrations(runtime).Select(service => $"{service.Lifetime} {Names.Service(service)} -> {Names.Implementation(service)}"), Counted: false),
        // The registration a single resolution returns: what a moved registration can change
        // without adding or removing anything.
        new("winner", runtime => Registrations(runtime).Where(service => service.Wins == true).Select(service => $"{Names.Service(service)} -> {Names.Implementation(service)}"), Merged: false),
        new("framework registrations", runtime => (runtime.FrameworkServices ?? []).Where(pair => pair.Key != FusionModulesAssembly).Select(pair => $"{pair.Key}: {pair.Value}"), Merged: false),
        new("hosted service", runtime => HostedServices(runtime).Select(HostedService)),
        new("DbContext", runtime => (runtime.DbContexts ?? []).Select(context =>
            $"{context.Type} ({context.Provider ?? "?"}: {context.ConnectionString ?? "no connection string"}; {context.Lifetime}{(context.Pooled ? ", pooled" : "")}" +
            $"; migrations {context.MigrationsAssembly ?? "-"} {context.MigrationsHistoryTable ?? "-"})"), Counted: false),
        // By stage and type: messages describe the moment, which a module changes. One gone is good news.
        new("error", runtime => runtime.Errors.Select(error => $"{error.Stage}: {error.Type ?? "error"}"), GoneIsNote: true),
    ];

    /// <summary>Every host of one inventory against the host of the same service in the other.</summary>
    public static ComparisonResult Compare(InventoryDocument before, InventoryDocument after)
    {
        var result = new ComparisonResult();
        var beforeHosts = before.Hosts.ToDictionary(host => host.Service, StringComparer.OrdinalIgnoreCase);
        var afterHosts = after.Hosts.ToDictionary(host => host.Service, StringComparer.OrdinalIgnoreCase);
        foreach (var name in beforeHosts.Keys.Union(afterHosts.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!beforeHosts.TryGetValue(name, out var was))
            {
                result.OnlyAfter.Add(name);
            }
            else if (!afterHosts.TryGetValue(name, out var now))
            {
                result.OnlyBefore.Add(name);
            }
            else
            {
                result.Hosts.Add(Host(name, [was], now, merged: false));
            }
        }

        return result;
    }

    /// <summary>
    /// One host of the second inventory against the services of the first that it took in: it serves
    /// the union of their endpoints and starts their hosted services, each no more times than one
    /// service did, and has every registration and DbContext one of them had — once, the programs'
    /// shared ones included, so those are compared as sets.
    /// </summary>
    public static ComparisonResult CompareMerged(InventoryDocument before, IReadOnlyCollection<string> services, InventoryDocument after, string host)
    {
        var sources = Names.Hosts(before, services, "the first inventory");
        var merged = Names.Hosts(after, [host], "the second inventory")[0];
        var result = new ComparisonResult();
        result.Hosts.Add(Host($"{merged.Service} = {string.Join(" + ", sources.Select(source => source.Service))}", sources, merged, merged: true));
        return result;
    }

    private static HostComparison Host(string name, IReadOnlyList<HostInfo> before, HostInfo after, bool merged)
    {
        var host = new HostComparison(name);
        foreach (var source in before.Where(source => source.BuildError is null != (after.BuildError is null)))
        {
            host.Differences.Add(after.BuildError is null ? $"build: failed ({source.Service}) -> succeeded" : $"build: succeeded ({source.Service}) -> failed");
        }

        if (before.Any(source => source.Runtime is null) || after.Runtime is null)
        {
            if (before.Any(source => source.Runtime is not null) || after.Runtime is not null)
            {
                host.Differences.Add($"probe: {string.Join(", ", before.Select(source => source.Runtime?.Status ?? "not run"))} -> {after.Runtime?.Status ?? "not run"}");
            }

            return host;
        }

        foreach (var section in Sections.Where(section => !merged || section.Merged))
        {
            // What each line may number in the host: as many as the one service that had most of it,
            // for a merge; there, only whether it is there at all, when the count is not the point.
            var setOnly = merged && !section.Counted;
            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var source in before)
            {
                foreach (var group in section.Lines(source.Runtime!).GroupBy(line => line, StringComparer.Ordinal))
                {
                    expected[group.Key] = setOnly ? 1 : Math.Max(expected.GetValueOrDefault(group.Key), group.Count());
                }
            }

            var actual = section.Lines(after.Runtime).GroupBy(line => line, StringComparer.Ordinal).ToDictionary(group => group.Key, group => setOnly ? 1 : group.Count(), StringComparer.Ordinal);
            foreach (var line in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
            {
                var was = expected.GetValueOrDefault(line);
                var now = actual.GetValueOrDefault(line);
                var gone = merged ? (now == 0 ? 1 : 0) : was - now;
                var added = now - was;
                for (var i = 0; i < gone; i++)
                {
                    (section.GoneIsNote ? host.Notes : host.Differences).Add($"- {section.Name} {line}");
                }

                for (var i = 0; i < added; i++)
                {
                    (section.AddedIsNote ? host.Notes : host.Differences).Add($"+ {section.Name} {line}");
                }
            }
        }

        // The application's hosted services start in registration order, and a module registers
        // before the program does: the same ones in another order are a difference of their own.
        if (!merged)
        {
            var was = StartOrder(before[0].Runtime!);
            var now = StartOrder(after.Runtime);
            if (!was.SequenceEqual(now) && was.Order(StringComparer.Ordinal).SequenceEqual(now.Order(StringComparer.Ordinal)))
            {
                host.Differences.Add($"start order of the application's hosted services: {string.Join(", ", was)} -> {string.Join(", ", now)}");
            }
        }

        return host;
    }

    private static List<string> StartOrder(ProbeOutput runtime) =>
        [.. HostedServices(runtime).Where(service => service.Application).Select(HostedService)];

    private static IEnumerable<ProbeService> Registrations(ProbeOutput runtime) =>
        (runtime.Services ?? []).Where(service => service.Assembly != FusionModulesAssembly && service.ServiceAssembly != FusionModulesAssembly);

    private static IEnumerable<ProbeHostedService> HostedServices(ProbeOutput runtime) =>
        (runtime.HostedServices ?? []).Where(service => service.Assembly != FusionModulesAssembly);

    private static string HostedService(ProbeHostedService service) =>
        service.Type ?? Names.WithAssembly(Names.Method(service.Factory ?? "(factory)"), service.Assembly);

    private static string Endpoint(ProbeEndpoint endpoint) =>
        $"{(endpoint.Methods is { Count: > 0 } methods ? string.Join(",", methods.Order(StringComparer.Ordinal)) : "*")} " +
        $"{(endpoint.Route is { } route ? "/" + route.TrimStart('/') : endpoint.DisplayName)} ({endpoint.Kind}" +
        $"{(endpoint.Assembly is null ? "" : ", " + endpoint.Assembly)}; " +
        $"{(endpoint.AllowAnonymous ? "anonymous" : endpoint.Authorization is { Count: > 0 } policies ? string.Join(", ", policies.Order(StringComparer.Ordinal)) : "no authorization")})";
}

internal sealed class ComparisonResult
{
    public List<HostComparison> Hosts { get; } = [];

    public List<string> OnlyBefore { get; } = [];

    public List<string> OnlyAfter { get; } = [];

    public bool Same => OnlyBefore.Count == 0 && OnlyAfter.Count == 0 && Hosts.All(host => host.Differences.Count == 0);

    public void Write(TextWriter writer, string beforeLabel, string afterLabel)
    {
        writer.WriteLine($"Comparing {beforeLabel} with {afterLabel}.");
        foreach (var host in Hosts)
        {
            writer.WriteLine();
            writer.WriteLine(host.Differences.Count == 0 ? $"{host.Service}: the same" : $"{host.Service}: {Count(host.Differences.Count, "difference")}");
            foreach (var difference in host.Differences)
            {
                writer.WriteLine($"  {difference}");
            }

            foreach (var note in host.Notes)
            {
                writer.WriteLine($"  note: {note}");
            }
        }

        foreach (var (services, label) in new[] { (OnlyBefore, beforeLabel), (OnlyAfter, afterLabel) })
        {
            foreach (var service in services)
            {
                writer.WriteLine();
                writer.WriteLine($"{service}: only in {label}");
            }
        }

        var different = Hosts.Count(host => host.Differences.Count > 0);
        writer.WriteLine();
        writer.WriteLine(
            $"{Count(Hosts.Count, "host")} compared: {Hosts.Count - different} the same, {different} different" +
            (OnlyBefore.Count + OnlyAfter.Count > 0 ? $"; {OnlyBefore.Count} only before, {OnlyAfter.Count} only after." : "."));
    }

    internal static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";
}

internal sealed class HostComparison(string service)
{
    public string Service { get; } = service;

    public List<string> Differences { get; } = [];

    public List<string> Notes { get; } = [];
}
