using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.Analyzers;

/// <summary>
/// Writes the project's module list into the compilation, so that code which needs every module
/// by name does not have to keep a copy of the list by hand.
/// </summary>
/// <remarks>
/// <para>
/// The case that asks for it is design time. <c>dotnet ef</c> builds the host, so HostingStartup
/// runs and the model follows <c>HOSTINGSTARTUPASSEMBLIES</c> as it stood in the shell that ran
/// the command: empty when unset, one topology's tables when set, and no error either way. An
/// <c>IDesignTimeDbContextFactory</c> therefore names the modules itself — and a hand-written list
/// is a copy of the project file that nothing keeps honest. Add a module, forget the array, and
/// the next migration is missing its tables with nothing to say so.
/// </para>
/// <para>
/// Only for a project that runs: a host or a test assembly. A module has no business knowing the
/// topology it will be deployed in, which is the point of the whole arrangement.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class KnownModulesGenerator : IIncrementalGenerator
{
    private const string EnabledOption = "build_property.FusionModulesKnownModules";
    private const string HintName = "FusionModules.KnownModules.g.cs";

    /// <summary>The step whose output decides whether anything is regenerated; named for tests.</summary>
    internal const string ModulesStep = "Modules";


    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
        {
            provider.GlobalOptions.TryGetValue(EnabledOption, out var enabled);

            return (
                Kind: ModuleFacts.GetProjectKind(provider.GlobalOptions),
                Disabled: string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
        });

        // The settings go in first, so that a module — most projects in a modular solution — or a
        // project that switched the list off pays for neither the entry point nor the reference
        // scan, edit after edit. A joined string rather than a collection: the value is what the
        // generated file contains, and it compares by value, so an edit that leaves the module
        // set alone regenerates nothing.
        var modules = context.CompilationProvider
            .Combine(settings)
            .Select(static (input, token) => Collect(input.Left, input.Right.Kind, input.Right.Disabled, token))
            .WithTrackingName(ModulesStep);

        context.RegisterSourceOutput(modules, static (production, modules) =>
        {
            if (modules is var (names, canValidate))
            {
                production.AddSource(HintName, SourceText.From(Render(names, canValidate), Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// The module list, and whether the hosting types <c>ValidateModules</c> is written against
    /// are there to compile it; or null when this project gets nothing. MSBuild classifies the
    /// project and this reads the answer, exactly as MOD0004 does; only a host or a test assembly
    /// gets the list. Without the package's targets there is no answer, and then producing a
    /// program is what separates a host or a test assembly from a module.
    /// </summary>
    private static (string Modules, bool CanValidate)? Collect(Compilation compilation, ProjectKind kind, bool disabled, CancellationToken token)
    {
        if (disabled ||
            kind == ProjectKind.Module ||
            (kind == ProjectKind.Unknown && compilation.GetEntryPoint(token) is null))
        {
            return null;
        }

        var hostingStartup = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);

        if (hostingStartup is null || moduleBase is null)
        {
            return (string.Empty, false);
        }

        var modules = ModuleFacts.GetReferencedModules(compilation, hostingStartup, moduleBase).ToList();
        var canValidate =
            compilation.GetTypeByMetadataName(ModuleFacts.HostApplicationBuilderMetadataName) is not null &&
            compilation.GetTypeByMetadataName(ModuleFacts.HostBuilderMetadataName) is not null &&
            compilation.GetTypeByMetadataName(ModuleFacts.WebHostDefaultsMetadataName) is not null;

        return (string.Join(";", Order(modules, token)), canValidate);
    }

    /// <summary>
    /// Dependencies first, ties broken by name.
    /// </summary>
    /// <remarks>
    /// Order is not decoration: <c>GetLoadedModules</c> preserves it, so a composing module's entity
    /// configurations are applied after the configurations of the entities it joins — and a
    /// composing module is a module that references the ones it joins, which is the edge sorted on
    /// here. The name breaks the remaining ties so that the same reference set always produces the
    /// same file, whatever order the compiler happened to hand the references over in.
    /// </remarks>
    private static List<string> Order(List<IAssemblySymbol> modules, CancellationToken token)
    {
        var names = new HashSet<string>(modules.Select(module => module.Identity.Name), StringComparer.Ordinal);

        var dependencies = modules.ToDictionary(
            module => module.Identity.Name,
            module => new HashSet<string>(
                module.Modules
                    .SelectMany(file => file.ReferencedAssemblies)
                    .Select(identity => identity.Name)
                    .Where(names.Contains),
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        var remaining = modules
            .Select(module => module.Identity.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var ordered = new List<string>(remaining.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            // A cycle cannot happen between assemblies, so the fallback is only here so that a
            // surprise produces a list rather than a hang.
            var next = remaining.FirstOrDefault(name => dependencies[name].All(emitted.Contains)) ?? remaining[0];

            ordered.Add(next);
            emitted.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }

    private static string Render(string modules, bool canValidate)
    {
        // new string[] rather than a collection expression: the file is compiled by the consumer,
        // whose LangVersion is theirs to choose, and generated code has no business raising it.
        var literals = modules.Length == 0
            ? "new string[0]"
            : "new string[] { " + string.Join(", ", modules.Split(';').Select(name => "\"" + name + "\"")) + " }";

        return $$"""
            // <auto-generated/>
            #nullable enable

            namespace FusionModules
            {
                /// <summary>
                /// Every module this project references, in activation order.
                /// </summary>
                /// <remarks>
                /// Generated by FusionModules from the project's references, so that code which needs
                /// the whole module set — an <c>IDesignTimeDbContextFactory</c>, a test that composes
                /// the union — cannot fall behind the project file. Dependencies come first, which is
                /// what puts a composing module after the modules it joins.
                /// </remarks>
                internal static class KnownModules
                {
                    /// <summary>
                    /// Every module, spelled as <c>HOSTINGSTARTUPASSEMBLIES</c> takes it.
                    /// </summary>
                    public const string All = "{{modules}}";

                    /// <summary>
                    /// Every module, one name per element, for <c>ModuleBase.CreateModuleRegistry</c>.
                    /// </summary>
                    public static string[] Names => {{literals}};
                }
            }
            {{(canValidate ? Validation : string.Empty)}}
            """;
    }

    /// <summary>
    /// The startup check, written against <c>KnownModules</c>. In <c>Microsoft.Extensions.Hosting</c>,
    /// beside the builder's own extensions, so that <c>builder.</c> finds it without a using.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core runs the hosting startups while it creates the builder, and for a name it cannot
    /// load it logs a critical message and carries on: the application comes up, passes its readiness
    /// probe and serves 404s. The modules cannot catch that themselves — when every name is wrong
    /// none of them is there to look — so the host does, against the list its references produced.
    /// Fully qualified throughout and no newer syntax than the file around it: the consumer's
    /// compiler and usings decide what this means.
    /// </remarks>
    internal const string Validation = $$"""

        namespace {{ModuleFacts.ValidateModulesNamespace}}
        {
            /// <summary>
            /// Checks <c>HOSTINGSTARTUPASSEMBLIES</c> against the modules this project references.
            /// </summary>
            internal static class {{ModuleFacts.ValidateModulesClassName}}
            {
                /// <summary>
                /// Fails startup when <c>HOSTINGSTARTUPASSEMBLIES</c> names an assembly that is not
                /// loaded in the process, or a module from <c>KnownModules</c> whose startup did not run.
                /// </summary>
                /// <remarks>
                /// Call it right after the builder is created: the hosting startups have run by then.
                /// A name that is not one of this project's modules — a third-party hosting startup —
                /// only has to load.
                /// </remarks>
                public static TBuilder {{ModuleFacts.ValidateModulesMethodName}}<TBuilder>(this TBuilder builder)
                    where TBuilder : global::Microsoft.Extensions.Hosting.IHostApplicationBuilder
                {
                    Validate(builder.Configuration);
                    return builder;
                }

                /// <summary>
                /// The same check for a generic host with a web host configured on it
                /// (<c>ConfigureWebHostDefaults</c>, <c>ConfigureWebHost</c>): it runs when the host is
                /// built, after the hosting startups, wherever in the chain it is called.
                /// </summary>
                public static global::Microsoft.Extensions.Hosting.IHostBuilder {{ModuleFacts.ValidateModulesMethodName}}(this global::Microsoft.Extensions.Hosting.IHostBuilder builder)
                {
                    return builder.ConfigureServices((context, services) => Validate(context.Configuration));
                }

                private static void Validate(global::Microsoft.Extensions.Configuration.IConfiguration configuration)
                {
                    var requested = configuration[global::Microsoft.AspNetCore.Hosting.WebHostDefaults.HostingStartupAssembliesKey] ?? string.Empty;

                    var loaded = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase);
                    foreach (var assembly in global::System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        loaded.Add(assembly.GetName().Name ?? string.Empty);
                    }

                    var started = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase);
                    foreach (var assembly in global::FusionModules.ModuleBase.GetLoadedModules(configuration))
                    {
                        started.Add(assembly.GetName().Name ?? string.Empty);
                    }

                    var known = new global::System.Collections.Generic.HashSet<string>(global::FusionModules.KnownModules.Names, global::System.StringComparer.OrdinalIgnoreCase);
                    var problems = new global::System.Collections.Generic.List<string>();

                    foreach (var entry in requested.Split(';'))
                    {
                        var name = entry.Trim();
                        if (name.Length == 0)
                        {
                            continue;
                        }

                        if (!loaded.Contains(name))
                        {
                            problems.Add("'" + name + "' could not be loaded. Check the spelling, and that the assembly is deployed next to the host.");
                        }
                        else if (known.Contains(name) && !started.Contains(name))
                        {
                            problems.Add("'" + name + "' is a module, but its startup did not run.");
                        }
                    }

                    if (problems.Count > 0)
                    {
                        throw new global::System.InvalidOperationException(
                            "HOSTINGSTARTUPASSEMBLIES = '" + requested + "'" + global::System.Environment.NewLine +
                            string.Join(global::System.Environment.NewLine, problems));
                    }
                }
            }
        }

        """;
}
