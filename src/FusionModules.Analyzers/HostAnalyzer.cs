using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// Rules about who may use a module's types. The host — the project with an entry point — may
/// reference modules, so that the build orders them and copies them to its output, but it must not
/// use their types (MOD0004) and must not pull them in through Application Part Discovery (MOD0007).
/// A library that is not a module must not use them either (MOD0005): wherever it goes, the module
/// goes with it. Modules may use each other; tests may use anything.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HostAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        Diagnostics.HostMustNotUseModuleTypes,
        Diagnostics.ApplicationPartMustNotNameModule,
        Diagnostics.LibraryMustNotUseModuleTypes,
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

        // A test that arranges data through a module's entity types is doing the right thing.
        // MSBuild classifies the project; this only reads the answer.
        if (ModuleFacts.GetProjectKind(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions) == ProjectKind.Test)
        {
            return;
        }

        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        if (hostingStartupAttribute is null || moduleBase is null)
        {
            return;
        }

        if (compilation.GetEntryPoint(context.CancellationToken) is not null)
        {
            CheckUsedReferences(context, hostingStartupAttribute, moduleBase, isHost: true);
            CheckApplicationParts(context, hostingStartupAttribute, moduleBase);
        }
        else if (!ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartupAttribute, moduleBase))
        {
            CheckUsedReferences(context, hostingStartupAttribute, moduleBase, isHost: false);
        }
    }

    private static void CheckUsedReferences(
        CompilationAnalysisContext context,
        INamedTypeSymbol hostingStartupAttribute,
        INamedTypeSymbol moduleBase,
        bool isHost)
    {
        // GetUsedAssemblyReferences, not References: a project reference that exists only to order
        // the build and copy the module to the output directory is exactly what we want people to
        // have. The used references are the assembly references the compiler writes into the output.
        var compilation = context.Compilation;
        var cheap = compilation.GetUsedAssemblyReferences(context.CancellationToken);
        if (FindModules(compilation, cheap, hostingStartupAttribute, moduleBase).Length == 0)
        {
            return;
        }

        // A compilation with errors cannot be analysed for used references — the compiler falls
        // back to reporting all of them — so every module the project merely references would be
        // reported, and the fix that suggests itself is to delete the references the model needs.
        // Checked only when something is about to be reported, which is not the normal path.
        if (HasErrors(context))
        {
            return;
        }

        var used = GetPreciselyUsedReferences(compilation, cheap, context.CancellationToken);
        var violations = FindModules(compilation, used, hostingStartupAttribute, moduleBase);
        if (violations.Length == 0)
        {
            return;
        }

        var usedLibraries = used
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .Where(assembly => !ModuleFacts.IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase))
            .ToArray();

        foreach (var module in violations)
        {
            // The compiler counts every assembly a used assembly references as used too. A module
            // that reaches this project only behind a library is that library's MOD0005, reported
            // in its own build where the fix is; saying it here as well would send people to the
            // wrong project.
            if (usedLibraries.Any(library => References(library, module)))
            {
                continue;
            }

            context.ReportDiagnostic(isHost
                ? Diagnostic.Create(Diagnostics.HostMustNotUseModuleTypes, Location.None, module.Name)
                : Diagnostic.Create(Diagnostics.LibraryMustNotUseModuleTypes, Location.None, compilation.AssemblyName, module.Name));
        }
    }

    private static IAssemblySymbol[] FindModules(
        Compilation compilation,
        ImmutableArray<MetadataReference> references,
        INamedTypeSymbol hostingStartupAttribute,
        INamedTypeSymbol moduleBase) =>
        references
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .Where(assembly => ModuleFacts.IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase))
            .ToArray();

    /// <summary>
    /// Used references as the compiler computes them when it tracks imports — what an ordinary
    /// build reports only when it also writes an XML documentation file.
    /// </summary>
    /// <remarks>
    /// Without <c>/doc</c> the parse options carry <c>DocumentationMode.None</c>, and then the
    /// compiler cannot tell a used import from an unused one: it counts every assembly that so much
    /// as contributes a type — internal and file-local included — to a namespace the project
    /// imports. A module with one class in <c>Microsoft.Extensions.Hosting</c>, or one built with an
    /// interceptor generator (a file-local attribute in <c>System.Runtime.CompilerServices</c>), is
    /// then "used" by every host that imports those namespaces. <c>DocumentationMode.Parse</c> is the
    /// least that switches import tracking on. Only the options change, so a <c>cref</c> to a
    /// module type is not counted: a comment does not put a module in a deployment. This binds the
    /// project a second time, and runs only when the cheap answer has already named a module.
    /// </remarks>
    private static ImmutableArray<MetadataReference> GetPreciselyUsedReferences(
        Compilation compilation,
        ImmutableArray<MetadataReference> cheap,
        CancellationToken cancellationToken)
    {
        if (compilation is not CSharpCompilation csharp ||
            csharp.SyntaxTrees.All(tree => tree.Options.DocumentationMode != DocumentationMode.None))
        {
            return cheap;
        }

        var trees = csharp.SyntaxTrees
            .Select(tree => tree.Options.DocumentationMode != DocumentationMode.None
                ? tree
                : tree.WithRootAndOptions(
                    tree.GetRoot(cancellationToken),
                    ((CSharpParseOptions)tree.Options).WithDocumentationMode(DocumentationMode.Parse)))
            .ToArray();

        return csharp.RemoveAllSyntaxTrees().AddSyntaxTrees(trees).GetUsedAssemblyReferences(cancellationToken);
    }

    private static bool References(IAssemblySymbol assembly, IAssemblySymbol module) =>
        assembly.Modules.Any(m => m.ReferencedAssemblySymbols.Any(r => SymbolEqualityComparer.Default.Equals(r, module)));

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
            .ToImmutableHashSet();

        if (declaredParts.IsEmpty)
        {
            return;
        }

        // The name first: it is the cheap test, and most references fail it.
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!declaredParts.Contains(assembly.Name) ||
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
