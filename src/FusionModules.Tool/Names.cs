using System.Text.RegularExpressions;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// How the inventory's names are spelled for comparing them — two inventories of one host, or the
/// hosts of one inventory with each other: what a conversion changes by design (a lambda that moved
/// from <c>Program</c> to <c>Module</c> or into a method of its own, a key made unique per run) is
/// spelled one way, and everything else as it is.
/// </summary>
internal static partial class Names
{
    /// <summary>
    /// What a registration resolves to, as the startup check tells implementations apart: a type, or
    /// the method a factory points at, each with its assembly — two services' copies of one type
    /// name are two types, and two programs' lambdas two factories.
    /// </summary>
    public static string Implementation(ProbeService service) =>
        WithAssembly(service.ImplementationType ?? (service.Factory is { } factory ? "factory " + Method(factory) : service.Implementation), service.Assembly);

    /// <summary>
    /// A factory's method as it is compared: a named method by its name, and a lambda — a local
    /// function, a closure — as a lambda. The method a lambda was written in is what a conversion
    /// changes: <c>Program</c>'s top-level statements before, <c>Module.ConfigureServices</c> or a
    /// method of the service's own after. With its assembly, which a conversion does not change, it
    /// is the same registration.
    /// </summary>
    public static string Method(string name) => name.Contains(".<", StringComparison.Ordinal) ? "lambda" : name;

    /// <summary>A name with the assembly it comes from.</summary>
    public static string WithAssembly(string name, string? assembly) => assembly is null ? name : $"{name} ({assembly})";

    /// <summary>A service type with its key, the key without the GUID Scrutor's <c>Decorate</c> makes per run.</summary>
    public static string Service(ProbeService service) =>
        service.Key is null ? service.ServiceType : $"{service.ServiceType} [key {Guid().Replace(service.Key, "*")}]";

    /// <summary>
    /// A route as the matcher tells routes apart: literals case-insensitively, parameters by their
    /// constraints and not their names — <c>/orders/{id}</c> and <c>/Orders/{orderId}</c> are one
    /// route, <c>/orders/{id:int}</c> another — a parameter with a default as optional, one leading
    /// slash. The runtime's route check keys endpoints the same way.
    /// </summary>
    public static string Route(string route) =>
        "/" + Parameter().Replace(route.Trim().Trim('/'), match =>
        {
            var constraints = match.Groups["constraints"].Value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var optional = match.Groups["optional"].Success || match.Groups["default"].Success;
            return "{" + (match.Groups["catchAll"].Length > 0 ? "*" : "") + string.Join(",", constraints) + (optional ? "?" : "") + "}";
        }).ToLowerInvariant();

    /// <summary>The hosts a command names, by service or project name; all of them when none is named.</summary>
    public static List<HostInfo> Hosts(InventoryDocument inventory, IReadOnlyCollection<string> names, string document = "the inventory") =>
        names.Count == 0
            ? [.. inventory.Hosts]
            : [.. names.Select(name => inventory.Hosts.FirstOrDefault(host =>
                    string.Equals(host.Service, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(host.Project), name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InventoryInputException(
                    $"No host '{name}' in {document}. Its hosts: {string.Join(", ", inventory.Hosts.Select(host => host.Service))}."))];

    [GeneratedRegex(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    // {id}, {id:int}, {id:int:min(1)}, {id?}, {page=1}, {*rest}, {**path}.
    [GeneratedRegex(@"\{(?<catchAll>\*{0,2})(?<name>[^{}:=?]*)(?<constraints>(?::[^{}=?:]+(?:\([^)]*\))?)*)(?<default>=[^{}?]*)?(?<optional>\?)?\}")]
    private static partial Regex Parameter();
}
