using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace FusionModules;

/// <summary>
/// Notices two modules mapping the same route, and says so once the pipeline is built.
/// </summary>
/// <remarks>
/// <para>
/// Two services could each answer <c>GET /health/details</c> or <c>POST /api/settings</c>; merged, both
/// endpoints are in one route table. Nothing fails at startup and the probe passes. The first request
/// to that address matches two endpoints and answers 500 with an <c>AmbiguousMatchException</c> — which
/// is the earliest anyone finds out, and only if somebody requests that address before a customer does.
/// </para>
/// <para>
/// A warning rather than a failure: two endpoints with one template are sometimes told apart by what the
/// comparison here does not model — a policy, an endpoint filter — and the host should not refuse to
/// start over a route the application may never serve. What it does model is what the matcher uses: the
/// template with parameter names ignored (<c>{id}</c> and <c>{orderId}</c> are the same route) but
/// constraints, optionality and catch-alls kept, the HTTP methods, the host, the accepted content
/// types and the order.
/// </para>
/// <para>
/// Whose an endpoint is comes from its handler: the assembly that declares the minimal API delegate's
/// method or the controller's action, written as the startup report writes it — the host's own
/// endpoints are <c>host</c>. Endpoints without one — health checks, endpoints built from a bare
/// <c>RequestDelegate</c> — have no owner here and are left out; so are two endpoints of one owner,
/// which is a bug the merge did not cause.
/// </para>
/// </remarks>
internal static class RouteCollisions
{
    /// <summary>Logs one warning listing the routes more than one owner maps.</summary>
    /// <param name="endpoints">The host's endpoints.</param>
    /// <param name="modules">The loaded modules' assembly names.</param>
    /// <param name="application">The host's own assembly name.</param>
    /// <param name="logger">Where the warning goes.</param>
    public static void Warn(IReadOnlyList<Endpoint> endpoints, IReadOnlySet<string> modules, string? application, ILogger logger)
    {
        var collisions = Find(endpoints, endpoint => EndpointTable.ModuleOrHostLabel(endpoint, modules, application) ?? EndpointTable.Owner(endpoint));
        if (collisions.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Routes mapped by more than one module match the same request, and answer it with 500 (AmbiguousMatchException): " +
            "{RouteCollisions}. Map them to different paths, or tell them apart by HTTP method, host, content type or Order.",
            string.Join("; ", collisions));
    }

    /// <summary>Each route several owners map, as <c>METHOD /template (Owner, Owner)</c>.</summary>
    internal static List<string> Find(IReadOnlyList<Endpoint> endpoints, Func<RouteEndpoint, string?> ownerOf)
    {
        var mapped = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching != true)
            .Select(endpoint => (Endpoint: endpoint, Owner: ownerOf(endpoint), Methods: Methods(endpoint)))
            .Where(entry => entry.Owner is not null)
            .ToList();

        var collisions = new List<string>();
        foreach (var route in mapped.GroupBy(entry => Key(entry.Endpoint), StringComparer.Ordinal))
        {
            var entries = route.ToList();
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            var methods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (entries[i].Owner == entries[j].Owner || Overlap(entries[i].Methods, entries[j].Methods) is not { } shared)
                    {
                        continue;
                    }

                    owners.Add(entries[i].Owner!);
                    owners.Add(entries[j].Owner!);
                    methods.UnionWith(shared);
                }
            }

            if (owners.Count > 1)
            {
                var verb = methods.Count == 0 ? "*" : string.Join(",", methods);
                // One spelling of the template, the same on every start.
                var template = entries.Select(entry => Template(entry.Endpoint.RoutePattern, forMatching: false)).Min(StringComparer.Ordinal);
                collisions.Add($"{verb} {template} ({string.Join(", ", owners)})");
            }
        }

        collisions.Sort(StringComparer.Ordinal);
        return collisions;
    }

    /// <summary>The HTTP methods the endpoint accepts; empty for any.</summary>
    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

    /// <summary>The methods two endpoints both accept, or null when they share none.</summary>
    private static IReadOnlyList<string>? Overlap(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var shared = first.Intersect(second, StringComparer.OrdinalIgnoreCase).ToList();
        return shared.Count > 0 ? shared : null;
    }

    /// <summary>
    /// What the matcher tells endpoints apart by, apart from the HTTP method. A conventional route's
    /// required values — <c>controller=Home</c> — are literals to the matcher, so two modules'
    /// <c>{controller}/{action}</c> actions are two routes.
    /// </summary>
    private static string Key(RouteEndpoint endpoint)
    {
        var hosts = endpoint.Metadata.GetMetadata<IHostMetadata>()?.Hosts ?? [];
        var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes ?? [];
        var required = endpoint.RoutePattern.RequiredValues
            .Where(value => value.Value is not null && !ReferenceEquals(value.Value, RoutePattern.RequiredValueAny))
            .Select(value => $"{value.Key}={Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture)}".ToLowerInvariant())
            .Order(StringComparer.Ordinal);
        return string.Join("|",
            endpoint.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Template(endpoint.RoutePattern, forMatching: true),
            string.Join(",", required),
            string.Join(",", hosts.Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(",", accepts.Order(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The template as the matcher sees it — literals case-insensitively, parameters by their
    /// constraints and not their names — or, for a reader, as it was written.
    /// </summary>
    private static string Template(RoutePattern pattern, bool forMatching) =>
        (forMatching ? string.Empty : "/") +
        string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => forMatching ? literal.Content.ToLowerInvariant() : literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter =>
                "{" + (parameter.IsCatchAll ? "*" : string.Empty) +
                (forMatching
                    ? string.Join(",", parameter.ParameterPolicies.Select(policy => policy.Content ?? policy.ParameterPolicy?.GetType().Name)) +
                      (parameter.IsOptional || parameter.Default is not null ? "?" : string.Empty)
                    : parameter.Name + (parameter.IsOptional ? "?" : string.Empty)) +
                "}",
            _ => string.Empty,
        }))));
}
