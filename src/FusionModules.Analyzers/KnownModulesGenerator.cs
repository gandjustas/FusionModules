using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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
    private const string KnownModulesMetadataName = "FusionModules.KnownModules";
    private const string HintName = "FusionModules.KnownModules.g.cs";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
        {
            provider.GlobalOptions.TryGetValue(EnabledOption, out var enabled);

            return (
                Kind: ModuleFacts.GetProjectKind(provider.GlobalOptions),
                Disabled: string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
        }).WithTrackingName(TrackingNames.KnownModulesSettings);

        // The settings go in first, so that a module — most projects in a modular solution — or a
        // project that switched the list off pays for neither the entry point nor the reference
        // scan, edit after edit. A joined string rather than a collection: the value is what the
        // generated file contains, and it compares by value, so an edit that leaves the module
        // set alone regenerates nothing.
        var modules = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(settings)
            .Select(static (input, token) => Collect(input.Left.Left, input.Left.Right, input.Right.Kind, input.Right.Disabled, token))
            .WithTrackingName(TrackingNames.KnownModules);

        context.RegisterSourceOutput(modules, static (production, modules) =>
        {
            if (modules is var (names, canValidate, alternatives))
            {
                production.AddSource(HintName, SourceText.From(Render(names, canValidate, alternatives), Encoding.UTF8));
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
    /// <remarks>
    /// The host's alternatives are read here, with its <c>.editorconfig</c>: a section applies to
    /// files, which only the compilation has, and only a project that gets the list pays for reading
    /// every file's answer.
    /// </remarks>
    private static (string Modules, bool CanValidate, string Alternatives)? Collect(
        Compilation compilation,
        AnalyzerConfigOptionsProvider options,
        ProjectKind kind,
        bool disabled,
        CancellationToken token)
    {
        if (disabled ||
            (kind != ProjectKind.Test && !ModuleFacts.IsHost(kind, compilation, token)))
        {
            return null;
        }

        // A test assembly its host grants InternalsVisibleTo sees the host's list and check already;
        // a second copy of each would make every use of them CS0436.
        if (compilation.SourceModule.ReferencedAssemblySymbols.Any(assembly =>
                assembly.GetTypeByMetadataName(KnownModulesMetadataName) is not null &&
                assembly.GivesAccessTo(compilation.Assembly)))
        {
            return null;
        }

        var hostingStartup = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);

        if (hostingStartup is null || moduleBase is null)
        {
            return (string.Empty, false, string.Empty);
        }

        var modules = ModuleFacts.GetReferencedModules(compilation, hostingStartup, moduleBase).ToList();
        var canValidate =
            compilation.GetTypeByMetadataName(ModuleFacts.HostApplicationBuilderMetadataName) is not null &&
            compilation.GetTypeByMetadataName(ModuleFacts.HostBuilderMetadataName) is not null &&
            compilation.GetTypeByMetadataName(ModuleFacts.WebHostDefaultsMetadataName) is not null;

        // Only ValidateModules hands the alternatives on, so without it they are not read. Joined in
        // an order that is the same on every run: the value compares by value.
        var alternatives = canValidate
            ? string.Join(";", ModuleFacts.ReadListOption(options, compilation.SyntaxTrees, ModuleFacts.AllowedServiceCollisionsOption).OrderBy(name => name, StringComparer.Ordinal))
            : string.Empty;

        // By name: a set, not an order. The order configurations are applied in comes from
        // GetLoadedModules at runtime, from the full assemblies; the references the compiler sees
        // here are reference assemblies, without the ones only a method body makes.
        return (
            string.Join(";", modules.Select(module => module.Identity.Name).OrderBy(name => name, StringComparer.Ordinal)),
            canValidate,
            alternatives);
    }

    private static string Render(string modules, bool canValidate, string alternatives)
    {
        // new string[] rather than a collection expression: the file is compiled by the consumer,
        // whose LangVersion is theirs to choose, and generated code has no business raising it.
        var literals = modules.Length == 0
            ? "new string[0]"
            : "new string[] { " + string.Join(", ", modules.Split(';').Select(Literal)) + " }";

        return $$"""
            // <auto-generated/>
            #nullable enable

            namespace FusionModules
            {
                /// <summary>
                /// Every module this project references, by name.
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
                    public const string All = {{Literal(modules)}};

                    /// <summary>
                    /// Every module, one name per element, for <c>ModuleBase.CreateModuleRegistry</c>.
                    /// </summary>
                    public static string[] Names => {{literals}};
                }
            }
            {{(canValidate ? Validation(alternatives) : string.Empty)}}
            """;
    }

    /// <summary>A C# string literal of <paramref name="value"/>.</summary>
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary>
    /// The startup check, written against <c>KnownModules</c>. In <c>Microsoft.Extensions.Hosting</c>,
    /// beside the builder's own extensions, so that <c>builder.</c> finds it without a using. With the
    /// host's <c>fusion_modules_allowed_service_collisions</c>, when it has one, <c>ValidateModules</c>
    /// also writes it under <see cref="TopologyConventions.AlternativeServicesKey"/> for the runtime's
    /// collision message; without, the file carries no trace of it.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core runs the hosting startups while it creates the builder, and for a name it cannot
    /// load it logs a critical message and carries on: the application comes up, passes its readiness
    /// probe and serves 404s. The modules cannot catch that themselves — when every name is wrong
    /// none of them is there to look — so the host does, against the list its references produced.
    /// Fully qualified throughout and no newer syntax than the file around it: the consumer's
    /// compiler and usings decide what this means.
    /// </remarks>
    internal static string Validation(string alternatives = "")
    {
        var (field, builderLine, contextLine) = alternatives.Length == 0
            ? (string.Empty, string.Empty, string.Empty)
            : ("\n\n" +
               "        // The contracts the host's build lists as implemented by alternative modules, one per\n" +
               "        // topology (fusion_modules_allowed_service_collisions). The modules read them only to\n" +
               "        // word the message when two of those load together after all: that still fails startup.\n" +
               "        private const string AlternativeServicesKey = " + Literal(TopologyConventions.AlternativeServicesKey) + ";\n" +
               "        private const string AlternativeServices = " + Literal(alternatives) + ";",
               "\n            builder.Configuration[AlternativeServicesKey] = AlternativeServices;",
               "\n                context.Configuration[AlternativeServicesKey] = AlternativeServices;");

        return $$"""

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
                /// <param name="builder">The builder.</param>
                /// <param name="runtimeTopologyChecks">
                /// <c>false</c> turns off the checks the modules run when the pipeline is built: two
                /// modules registering one service, options configured differently, routes that collide,
                /// and the endpoint counts in the startup report. The check of
                /// <c>HOSTINGSTARTUPASSEMBLIES</c> itself always runs.
                /// </param>
                public static TBuilder {{ModuleFacts.ValidateModulesMethodName}}<TBuilder>(this TBuilder builder, bool runtimeTopologyChecks = true)
                    where TBuilder : global::Microsoft.Extensions.Hosting.IHostApplicationBuilder
                {
                    Validate(builder.Configuration);{{builderLine}}
                    if (!runtimeTopologyChecks)
                    {
                        builder.Configuration[RuntimeTopologyChecksKey] = "false";
                    }

                    return builder;
                }

                /// <summary>
                /// The same check for a generic host with a web host configured on it
                /// (<c>ConfigureWebHostDefaults</c>, <c>ConfigureWebHost</c>): it runs when the host is
                /// built, after the hosting startups, wherever in the chain it is called.
                /// </summary>
                public static global::Microsoft.Extensions.Hosting.IHostBuilder {{ModuleFacts.ValidateModulesMethodName}}(this global::Microsoft.Extensions.Hosting.IHostBuilder builder, bool runtimeTopologyChecks = true)
                {
                    return builder.ConfigureServices((context, services) =>
                    {
                        Validate(context.Configuration);{{contextLine}}
                        if (!runtimeTopologyChecks)
                        {
                            context.Configuration[RuntimeTopologyChecksKey] = "false";
                        }
                    });
                }

                // Read by the modules when the pipeline is built; the same key works from
                // appsettings.json or the environment.
                private const string RuntimeTopologyChecksKey = {{Literal(TopologyConventions.RuntimeTopologyChecksKey)}};{{field}}

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
}
