using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// A program whose own registrations and endpoints can move into a module of the same project (MOD0015).
/// </summary>
/// <remarks>
/// <para>
/// Not a defect: a pointer to the conversion, carried by a diagnostic so that its fix runs where
/// refactorings do not — <c>dotnet format</c>, a script, an agent. Turning a service's
/// <c>Program.cs</c> or <c>Startup.cs</c> into a module in place is the first step of moving a
/// microservice into a modular monolith; the project stays a program and runs as before, because
/// ASP.NET Core runs the entry assembly's own hosting startup.
/// </para>
/// <para>
/// Reported in a host — a program, not a test project — that references the package, has no module
/// of its own yet and references no module: a host composing modules is not a service on its way to
/// becoming one. The report is on the first statement that would move, and only when one would.
/// </para>
/// <para>
/// "Own code" here is the compilation's own source, and when nothing of it moves, every project
/// reference too, whose methods it takes at their word: only the source is read, in an IDE as in a
/// build. The fix counts only the references no other program of the solution shares — a service's
/// Infrastructure project — and reads their methods, so it may move more or less than the count says.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProgramConversionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.ProgramCanBecomeModule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        var hostingStartup = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        if (moduleBase is null ||
            hostingStartup is null ||
            SymbolEqualityComparer.Default.Equals(moduleBase.ContainingAssembly, compilation.Assembly) ||
            !ModuleFacts.IsHost(compilation, context.Options.AnalyzerConfigOptionsProvider.GlobalOptions, context.CancellationToken) ||
            ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartup, moduleBase) ||
            ModuleFacts.GetReferencedModules(compilation, hostingStartup, moduleBase).Any() ||
            ProgramConversion.Types.For(compilation) is not { } types)
        {
            return;
        }

        var projects = ProjectReferences(compilation, context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
        context.RegisterSemanticModelAction(modelContext =>
        {
            if (modelContext.SemanticModel.SyntaxTree.GetRoot(modelContext.CancellationToken) is CompilationUnitSyntax root &&
                ProgramConversion.MightApply(root) &&
                Analyze(modelContext.SemanticModel, root, types, projects, modelContext.CancellationToken) is { } plan)
            {
                modelContext.ReportDiagnostic(Diagnostic.Create(Diagnostics.ProgramCanBecomeModule, plan.First.GetLocation(), plan.Count));
            }
        });
    }

    /// <summary>
    /// What moves as a build sees the project. Its source first: a layered service — an API over
    /// its Application and Infrastructure projects — registers nothing of its source's own, and is
    /// still one the fix converts, so then its project references count as its own code too. The
    /// fix sees which of them another program shares.
    /// </summary>
    internal static ConversionPlan? Analyze(
        SemanticModel model, CompilationUnitSyntax root, ProgramConversion.Types types, HashSet<string> projects, CancellationToken cancellationToken)
    {
        var own = model.Compilation.Assembly;
        return ProgramConversion.Analyze(model, root, types, assembly => SymbolEqualityComparer.Default.Equals(assembly, own), cancellationToken) ??
            (projects.Count == 0
                ? null
                : ProgramConversion.Analyze(
                    model,
                    root,
                    types,
                    assembly => SymbolEqualityComparer.Default.Equals(assembly, own) || projects.Contains(TopologyConventions.WithoutWhitespace(assembly.Identity.Name)),
                    cancellationToken));
    }

    /// <summary>
    /// The assemblies of the project's project references: from the package's targets
    /// (<c>FusionModulesProjectReferences</c>), and in a workspace from the compilations it
    /// references, which is how an IDE or <c>dotnet format</c> passes a project reference. Without
    /// whitespace, as <see cref="TopologyConventions.SplitNames"/> gives them.
    /// </summary>
    internal static HashSet<string> ProjectReferences(Compilation compilation, AnalyzerConfigOptions globalOptions)
    {
        globalOptions.TryGetValue(ModuleFacts.ProjectReferencesOption, out var listed);
        var names = new HashSet<string>(TopologyConventions.SplitNames(listed), StringComparer.OrdinalIgnoreCase);
        names.UnionWith(compilation.References.OfType<CompilationReference>()
            .Select(reference => TopologyConventions.WithoutWhitespace(reference.Compilation.AssemblyName ?? string.Empty)));
        names.Remove(string.Empty);
        return names;
    }
}
