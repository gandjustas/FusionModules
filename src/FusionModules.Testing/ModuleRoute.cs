namespace FusionModules.Testing;

/// <summary>One route a host serves: the module that serves it, an HTTP method and a pattern.</summary>
/// <param name="Module">
/// The assembly name of the loaded module that serves the route; <see cref="Host"/> when the host's
/// own assembly serves it, whether or not that assembly is a module too, as the startup report counts
/// it; null when the framework or a library serves it (an OpenAPI document, a health check, a static
/// asset) — the module that mapped such a route is not on its endpoint.
/// </param>
/// <param name="Method">The HTTP method, or <see cref="AnyMethod"/> for a route that takes every method.</param>
/// <param name="Pattern">The route pattern as written, starting with a slash: <c>/orders/{id:int}</c>.</param>
/// <remarks>
/// One method per entry, so that two inventories compare by value, a collection assertion reads
/// like the route table, and <see cref="ToString"/> is a line of a snapshot.
/// </remarks>
public sealed record ModuleRoute(string? Module, string Method, string Pattern)
{
    /// <summary>The <see cref="Method"/> of a route that matches any HTTP method.</summary>
    public const string AnyMethod = "*";

    /// <summary>
    /// The <see cref="Module"/> of a route the host's own assembly serves —
    /// <c>IHostEnvironment.ApplicationName</c> — whether or not it is a module too: <c>host</c>, as the
    /// startup report's endpoints part writes it.
    /// </summary>
    /// <remarks>
    /// Only when a loaded module's assembly is itself named <c>host</c> is the host's route named by
    /// its assembly instead, so that the two stay apart.
    /// </remarks>
    public const string Host = "host";

    /// <summary>The route as <c>GET /orders/{id} (OrdersModule)</c>.</summary>
    /// <returns>The method, the pattern and the module in parentheses, if there is one.</returns>
    public override string ToString() =>
        Module is null ? $"{Method} {Pattern}" : $"{Method} {Pattern} ({Module})";
}
