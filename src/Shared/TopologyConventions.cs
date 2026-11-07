namespace FusionModules;

/// <summary>
/// What the startup checks and the analyzers have to agree on, written once: the configuration keys
/// the generated <c>ValidateModules</c> writes and the modules read, the name of the marker
/// attribute, the services several registrations share by design, and how a list of type names is
/// split and compared.
/// </summary>
/// <remarks>
/// Compiled into the FusionModules package and into its analyzers (netstandard2.0), each as an
/// internal type of its own; neither sees the other's internals, so the two copies never meet. A key
/// spelled differently in the two used to be a setting that silently did nothing.
/// </remarks>
internal static class TopologyConventions
{
    /// <summary>
    /// <c>false</c> turns the startup checks off: service and options collisions before the
    /// pipeline is built, route collisions and the endpoint counts after.
    /// <c>ValidateModules(runtimeTopologyChecks: false)</c> writes it; appsettings.json or the
    /// environment can too.
    /// </summary>
    public const string RuntimeTopologyChecksKey = "FusionModules:RuntimeTopologyChecks";

    /// <summary>Service types whose overlap is intended — names, as a list or separated by <c>;</c>.</summary>
    public const string AllowedServiceCollisionsKey = "FusionModules:AllowedServiceCollisions";

    /// <summary>
    /// The contracts the host's build lists as implemented by alternative modules, one per topology —
    /// its <c>fusion_modules_allowed_service_collisions</c>, which the generated <c>ValidateModules</c>
    /// writes here. Read only to word the collision message: a collision on such a contract still
    /// fails startup.
    /// </summary>
    public const string AlternativeServicesKey = "FusionModules:AlternativeServices";

    /// <summary>
    /// The attribute a contract carries when several modules implement it on purpose, matched by
    /// name in any namespace, so that a contracts library declares its own instead of referencing
    /// this package.
    /// </summary>
    public const string MultipleImplementationsAttributeName = "MultipleImplementationsAttribute";

    /// <summary>
    /// Library service types several registrations share by design, outside the framework's own
    /// assemblies: EF Core's non-generic <c>DbContextOptions</c>, which <c>AddDbContext</c> registers
    /// for every context and which is necessarily ambiguous with two contexts in one container.
    /// </summary>
    public static readonly string[] SharedByDesign = ["Microsoft.EntityFrameworkCore.DbContextOptions"];

    /// <summary>
    /// Assemblies whose service types the collision checks leave alone. The framework registers most
    /// of its services through <c>TryAddEnumerable</c>, as extension points consumed as a sequence —
    /// one module's <c>AddControllers</c> and another's <c>AddRazorPages</c> each add an
    /// <c>IActionDescriptorProvider</c>, and both are meant to be there — and its <c>TryAdd</c>
    /// discipline is what makes them safe to share. The checks are aimed at the application's types
    /// and its libraries'.
    /// </summary>
    private static readonly string[] FrameworkAssemblyPrefixes =
    [
        "System.",
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
        "Microsoft.Net.",
        "Microsoft.JSInterop",
        "Microsoft.Win32.",
        "netstandard",
        "mscorlib",
    ];

    /// <summary>Whether an assembly, by its simple name, is one of the framework's (<see cref="FrameworkAssemblyPrefixes"/>).</summary>
    public static bool IsFrameworkAssembly(string? name) =>
        name is not null &&
        (name == "System" || FrameworkAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>
    /// A name with its whitespace removed. Type names have none of their own; what there is comes
    /// from how a person separates generic arguments, and both sides of a comparison drop it.
    /// </summary>
    public static string WithoutWhitespace(string name) =>
        name.Any(char.IsWhiteSpace) ? string.Concat(name.Where(c => !char.IsWhiteSpace(c))) : name;

    /// <summary>
    /// The entries of a list of type names: split at <c>;</c>, and at <c>,</c> outside angle
    /// brackets — <c>IHandler&lt;A, B&gt;</c> and <c>Dictionary&lt;,&gt;</c> are one entry each — with
    /// whitespace dropped and empty entries left out.
    /// </summary>
    public static IEnumerable<string> SplitNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var depth = 0;
        var start = 0;
        for (var i = 0; i <= value!.Length; i++)
        {
            var c = i < value.Length ? value[i] : ';';
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ';' || (c == ',' && depth == 0))
            {
                var entry = WithoutWhitespace(value.Substring(start, i - start));
                if (entry.Length > 0)
                {
                    yield return entry;
                }

                start = i + 1;
            }
        }
    }
}
