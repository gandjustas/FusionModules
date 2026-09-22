using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// Rules that apply to the host — the project with an entry point. It may reference modules, so
/// that the build orders them and copies them to its output, but it must not use their types
/// (MOD0003) and must not pull them in through Application Part Discovery (MOD0004).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HostAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        Diagnostics.HostMustNotUseModuleTypes,
        Diagnostics.ApplicationPartMustNotNameModule,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(OnCompilation);
    }

    private const string ProjectKindOption = "build_property.FusionModulesProjectKind";

    private static void OnCompilation(CompilationAnalysisContext context)
    {
        var compilation = context.Compilation;

        // An entry point is how a host is recognised, but a test project has one too — and a test
        // that arranges data through a module's entity types is doing the right thing. MSBuild
        // classifies the project; this only reads the answer.
        if (compilation.GetEntryPoint(context.CancellationToken) is null || IsTestProject(context))
        {
            return;
        }

        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        if (hostingStartupAttribute is null || moduleBase is null)
        {
            return;
        }

        CheckUsedReferences(context, hostingStartupAttribute, moduleBase);
        CheckApplicationParts(context, hostingStartupAttribute, moduleBase);
    }

    private static void CheckUsedReferences(
        CompilationAnalysisContext context,
        INamedTypeSymbol hostingStartupAttribute,
        INamedTypeSymbol moduleBase)
    {
        // GetUsedAssemblyReferences, not References: a project reference that exists only to order
        // the build and copy the module to the output directory is exactly what we want people to have.
        var used = context.Compilation.GetUsedAssemblyReferences(context.CancellationToken);

        var violations = used
            .Select(context.Compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .Where(assembly => ModuleFacts.IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase))
            .ToArray();

        // A compilation with errors cannot be analysed for used references — the compiler falls
        // back to reporting all of them — so every module the host merely references would be
        // reported, and the fix that suggests itself is to delete the references the model needs.
        // Checked only when something is about to be reported, which is not the normal path.
        if (violations.Length == 0 || HasErrors(context))
        {
            return;
        }

        foreach (var assembly in violations)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.HostMustNotUseModuleTypes,
                Location.None,
                assembly.Name));
        }
    }

    private static bool IsTestProject(CompilationAnalysisContext context) =>
        context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(ProjectKindOption, out var kind) &&
        string.Equals(kind, "Test", StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(CompilationAnalysisContext context) =>
        context.Compilation
            .GetDiagnostics(context.CancellationToken)
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));

    private static void CheckApplicationParts(
        CompilationAnalysisContext context,
        INamedTypeSymbol hostingStartupAttribute,
        INamedTypeSymbol moduleBase)
    {
        var compilation = context.Compilation;
        var applicationPartAttribute = compilation.GetTypeByMetadataName(ModuleFacts.ApplicationPartAttributeMetadataName);
        if (applicationPartAttribute is null)
        {
            return;
        }

        var declaredParts = ModuleFacts.GetAttributes(compilation.Assembly, applicationPartAttribute)
            .Select(attribute => attribute.ConstructorArguments.Length == 1
                ? attribute.ConstructorArguments[0].Value as string
                : null)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToImmutableHashSet()!;

        if (declaredParts.IsEmpty)
        {
            return;
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                !declaredParts.Contains(assembly.Name) ||
                !ModuleFacts.IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ApplicationPartMustNotNameModule,
                Location.None,
                assembly.Name));
        }
    }
}
