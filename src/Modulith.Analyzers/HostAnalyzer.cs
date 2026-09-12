using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Modulith.Analyzers;

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

    private static void OnCompilation(CompilationAnalysisContext context)
    {
        var compilation = context.Compilation;
        if (compilation.GetEntryPoint(context.CancellationToken) is null)
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
        foreach (var reference in context.Compilation.GetUsedAssemblyReferences(context.CancellationToken))
        {
            if (context.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                !ModuleFacts.IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.HostMustNotUseModuleTypes,
                Location.None,
                assembly.Name));
        }
    }

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
