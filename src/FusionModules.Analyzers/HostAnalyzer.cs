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
        var kind = ModuleFacts.GetProjectKind(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
        if (kind == ProjectKind.Test)
        {
            return;
        }

        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        if (hostingStartupAttribute is null || moduleBase is null)
        {
            return;
        }

        if (ModuleFacts.IsHost(kind, compilation, context.CancellationToken))
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

        // Used references are a subset of references, and asking for them binds every method body:
        // a project that references no module has nothing to find and does not pay for looking.
        if (!ModuleFacts.GetReferencedModules(compilation, hostingStartupAttribute, moduleBase).Any())
        {
            return;
        }

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

        var usedAssemblies = used
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .ToArray();

        foreach (var module in violations)
        {
            // The compiler counts every assembly a used assembly references as used too. A module
            // that reaches this project behind another assembly — a library, whose own build
            // reports it as MOD0005, or a module that composes it — is not a line in this project:
            // the assembly in front is reported, and fixing that removes this one too.
            if (usedAssemblies.Any(carrier => !SymbolEqualityComparer.Default.Equals(carrier, module) && References(carrier, module)))
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
    /// Used references as the compiler computes them when it tracks imports and ignores
    /// documentation comments: the assemblies the code itself uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without <c>/doc</c> the parse options carry <c>DocumentationMode.None</c>, and then the
    /// compiler cannot tell a used import from an unused one: it counts every assembly that so much
    /// as contributes a type — internal and file-local included — to a namespace the project
    /// imports. A module with one class in <c>Microsoft.Extensions.Hosting</c>, or one built with an
    /// interceptor generator (a file-local attribute in <c>System.Runtime.CompilerServices</c>), is
    /// then "used" by every host that imports those namespaces. <c>DocumentationMode.Parse</c> is the
    /// least that switches import tracking on.
    /// </para>
    /// <para>
    /// With documentation comments parsed — in the IDE always, and in a build that writes an XML
    /// documentation file — a <c>cref</c> to a module's type counts as use. A comment does not put a
    /// module in a deployment, so parsed documentation comments are taken out first.
    /// </para>
    /// <para>
    /// This binds the project a second time, and runs only when the cheap answer has already named
    /// a module.
    /// </para>
    /// </remarks>
    private static ImmutableArray<MetadataReference> GetPreciselyUsedReferences(
        Compilation compilation,
        ImmutableArray<MetadataReference> cheap,
        CancellationToken cancellationToken)
    {
        if (compilation is not CSharpCompilation csharp)
        {
            return cheap;
        }

        var changed = false;
        var trees = csharp.SyntaxTrees.Select(tree =>
        {
            var root = tree.GetRoot(cancellationToken);
            var comments = root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                                                   trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)).ToArray();
            var options = (CSharpParseOptions)tree.Options;
            if (comments.Length == 0 && options.DocumentationMode != DocumentationMode.None)
            {
                return tree;
            }

            changed = true;
            return tree.WithRootAndOptions(
                comments.Length == 0 ? root : root.ReplaceTrivia(comments, (_, _) => default),
                options.DocumentationMode == DocumentationMode.None ? options.WithDocumentationMode(DocumentationMode.Parse) : options);
        }).ToArray();

        return changed
            ? csharp.RemoveAllSyntaxTrees().AddSyntaxTrees(trees).GetUsedAssemblyReferences(cancellationToken)
            : cheap;
    }

    private static bool References(IAssemblySymbol assembly, IAssemblySymbol module) =>
        assembly.Modules.Any(m => m.ReferencedAssemblySymbols.Any(r => SymbolEqualityComparer.Default.Equals(r, module)));

    private static bool HasErrors(CompilationAnalysisContext context) =>
        context.Compilation
            .GetDiagnostics(context.CancellationToken)
            // A warning promoted by TreatWarningsAsErrors does not stop the compiler from working out
            // which references are used, and must not silence the rule.
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                               !diagnostic.IsWarningAsError &&
                               diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));

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
