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
/// goes with it. Modules may use each other; tests may use anything. And two of the host's modules
/// should not register one service with different implementations (MOD0014).
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
        Diagnostics.ModulesRegisterOneServiceDifferently,
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

        var isHost = ModuleFacts.IsHost(kind, compilation, context.CancellationToken);
        if (!isHost && ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartupAttribute, moduleBase))
        {
            return;
        }

        // Every check here is about the modules the project references: one that references none
        // has nothing to find and does not pay for looking.
        var modules = ModuleFacts.GetReferencedModules(compilation, hostingStartupAttribute, moduleBase).ToList();
        if (modules.Count == 0)
        {
            return;
        }

        CheckUsedReferences(context, hostingStartupAttribute, moduleBase, isHost);
        if (isHost)
        {
            CheckApplicationParts(context, modules);
            CheckRegistrations(context, modules);
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
        // Used references are a subset of references, and asking for them binds every method body.
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

    private static void CheckApplicationParts(CompilationAnalysisContext context, List<IAssemblySymbol> modules)
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

        foreach (var module in modules.Where(module => declaredParts.Contains(module.Name)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ApplicationPartMustNotNameModule,
                Location.None,
                module.Name));
        }
    }

    /// <summary>
    /// Two modules registering one service with different implementations, read from what each
    /// module recorded about itself (<see cref="ModuleRecords"/>).
    /// </summary>
    /// <remarks>
    /// The same question the runtime asks at startup, asked of every module the host references
    /// rather than of a topology: the build cannot know which modules a deployment loads together,
    /// so modules that are alternatives — one per topology — are recorded as such in
    /// <c>fusion_modules_allowed_service_collisions</c>. A warning rather than an error for that
    /// reason, and because what a module's code says is a candidate: a registration may sit on a
    /// path that never runs.
    /// </remarks>
    private static void CheckRegistrations(CompilationAnalysisContext context, List<IAssemblySymbol> modules)
    {
        var registrations = modules.SelectMany(ModuleRecords.Registrations).ToList();

        if (registrations.Count == 0)
        {
            return;
        }

        ImmutableHashSet<string>? allowed = null;

        foreach (var service in registrations.GroupBy(entry => entry.Registration.ServiceIdentity).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            // The same implementation registered by two modules is redundant rather than wrong:
            // whichever wins, every module gets what it asked for.
            var byModule = service
                .GroupBy(entry => entry.Module)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (Module: group.Key, Implementations: group.Select(entry => entry.Registration.ImplementationIdentity).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList()))
                .ToList();

            if (byModule.Count < 2 || byModule.All(module => module.Implementations.SequenceEqual(byModule[0].Implementations)))
            {
                continue;
            }

            // The option as any of the host's files sees it: an .editorconfig section applies to files.
            allowed ??= ModuleFacts.ReadListOption(context.Options.AnalyzerConfigOptionsProvider, context.Compilation.SyntaxTrees, ModuleFacts.AllowedServiceCollisionsOption);
            if (service.First().Registration.Spellings.Split(ModuleRecordsGenerator.SpellingSeparator).Any(allowed.Contains))
            {
                continue;
            }

            // Implementations by their readable names, unless two different ones would read the same.
            var display = service
                .GroupBy(entry => entry.Registration.ImplementationIdentity)
                .ToDictionary(group => group.Key, group => group.First().Registration.Implementation);
            var ambiguous = display.Values.Distinct().Count() < display.Count;

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ModulesRegisterOneServiceDifferently,
                Location.None,
                service.First().Registration.Service,
                string.Join("; ", byModule.Select(module =>
                    $"{module.Module}: {string.Join(", ", module.Implementations.Select(identity => ambiguous ? identity : display[identity]))}"))));
        }
    }
}
