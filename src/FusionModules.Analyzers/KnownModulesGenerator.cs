using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
            if (modules is not null)
            {
                production.AddSource(HintName, SourceText.From(Render(modules), Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// The module list, or null when this project gets none. MSBuild classifies the project and
    /// this reads the answer, exactly as MOD0003 does; only a host or a test assembly gets the
    /// list. Without the package's targets there is no answer, and then producing a program is
    /// what separates a host or a test assembly from a module.
    /// </summary>
    private static string? Collect(Compilation compilation, ProjectKind kind, bool disabled, CancellationToken token)
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
            return string.Empty;
        }

        var modules = ModuleFacts.GetReferencedModules(compilation, hostingStartup, moduleBase).ToList();

        return string.Join(";", Order(modules, token));
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

    private static string Render(string modules)
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

            """;
    }
}
