using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// Rules that apply to a module assembly: it keeps its types to itself (MOD0001), the type it
/// names in <c>[assembly: HostingStartup]</c> really is a module (MOD0002), and every module it
/// declares is actually registered (MOD0003).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleAnalyzer : DiagnosticAnalyzer
{
    private const string AllowedPublicTypesOption = "fusion_modules_allowed_public_types";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        Diagnostics.ModuleMustNotExposePublicTypes,
        Diagnostics.HostingStartupTypeMustBeModule,
        Diagnostics.ModuleMustBeRegistered,
        Diagnostics.HubClientTypeMustBeReachable,
        Diagnostics.PackageNotReferenced,
    ];

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
        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        if (hostingStartupAttribute is null)
        {
            return;
        }

        var declaredStartups = ModuleFacts.GetHostingStartupTypes(compilation.Assembly, hostingStartupAttribute).ToArray();
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);

        // MOD0002 / MOD0013 — check what the assembly declares.
        if (declaredStartups.Length > 0)
        {
            RegisterHostingStartupAttributeCheck(context, hostingStartupAttribute, moduleBase);
        }

        if (moduleBase is null)
        {
            return;
        }

        // MOD0003 — every module type must be named by an attribute.
        var registered = new HashSet<ISymbol>(declaredStartups, SymbolEqualityComparer.Default);
        context.RegisterSymbolAction(
            symbolContext => CheckModuleIsRegistered(symbolContext, moduleBase, registered),
            SymbolKind.NamedType);

        // MOD0001 — only applies to a module assembly, and never to the host's own executable.
        if (!ModuleFacts.IsModuleCompilation(compilation, hostingStartupAttribute, moduleBase, context.CancellationToken))
        {
            return;
        }

        var declared = ModuleFacts.ScanDeclaredTypes(compilation);
        var hubClients = declared.HubClients;

        var exemptions = new TypeExemptions(compilation, declared);
        var allowed = new AllowedPublicTypes(context.Options.AnalyzerConfigOptionsProvider);
        context.RegisterSymbolAction(
            symbolContext => CheckTypeIsNotPublic(symbolContext, exemptions, allowed),
            SymbolKind.NamedType);

        // MOD0010 — the mirror image: these types the runtime insists on reaching, and an
        // InternalsVisibleTo to the proxy's assembly is the cheaper of the two ways to allow it.
        if (hubClients.Count > 0 &&
            !ModuleFacts.HasInternalsVisibleTo(compilation, ModuleFacts.TypedClientBuilderAssemblyName))
        {
            context.RegisterSymbolAction(
                symbolContext => CheckHubClientIsReachable(symbolContext, hubClients),
                SymbolKind.NamedType);
        }
    }

    private static void RegisterHostingStartupAttributeCheck(
        CompilationStartAnalysisContext context,
        INamedTypeSymbol hostingStartupAttribute,
        INamedTypeSymbol? moduleBase)
    {
        context.RegisterSyntaxNodeAction(
            syntaxContext =>
            {
                var attributeSyntax = (AttributeSyntax)syntaxContext.Node;
                if (attributeSyntax.Parent is not AttributeListSyntax { Target.Identifier.RawKind: (int)SyntaxKind.AssemblyKeyword })
                {
                    return;
                }

                var attributeType = syntaxContext.SemanticModel.GetTypeInfo(attributeSyntax, syntaxContext.CancellationToken).Type;
                if (!SymbolEqualityComparer.Default.Equals(attributeType, hostingStartupAttribute))
                {
                    return;
                }

                // Without the runtime package there is no ModuleBase to inherit from, and reporting
                // "does not derive from ModuleBase" would be actively misleading. Say what is wrong.
                if (moduleBase is null)
                {
                    syntaxContext.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.PackageNotReferenced,
                        attributeSyntax.GetLocation(),
                        "FusionModules"));
                    return;
                }

                var argument = attributeSyntax.ArgumentList?.Arguments.FirstOrDefault();
                if (argument?.Expression is not TypeOfExpressionSyntax typeOf)
                {
                    return;
                }

                var moduleType = syntaxContext.SemanticModel.GetTypeInfo(typeOf.Type, syntaxContext.CancellationToken).Type;
                if (moduleType is null or IErrorTypeSymbol || ModuleFacts.InheritsFrom(moduleType, moduleBase))
                {
                    return;
                }

                syntaxContext.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.HostingStartupTypeMustBeModule,
                    typeOf.Type.GetLocation(),
                    moduleType.Name,
                    ModuleFacts.ModuleBaseMetadataName));
            },
            SyntaxKind.Attribute);
    }

    private static void CheckModuleIsRegistered(
        SymbolAnalysisContext context,
        INamedTypeSymbol moduleBase,
        HashSet<ISymbol> registered)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.IsAbstract ||
            !ModuleFacts.InheritsFrom(type, moduleBase) ||
            registered.Contains(type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.ModuleMustBeRegistered,
            type.Locations.FirstOrDefault() ?? Location.None,
            type.Name));
    }

    private static void CheckTypeIsNotPublic(SymbolAnalysisContext context, TypeExemptions exemptions, AllowedPublicTypes allowed)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.DeclaredAccessibility != Accessibility.Public ||
            type.ContainingType is not null ||
            !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, context.Compilation.Assembly) ||
            exemptions.IsExempt(type) ||
            allowed.Allows(type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.ModuleMustNotExposePublicTypes,
            type.Locations.FirstOrDefault() ?? Location.None,
            type.Name));
    }

    private static void CheckHubClientIsReachable(
        SymbolAnalysisContext context,
        Dictionary<ITypeSymbol, string> hubClients)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // Declared elsewhere means it cannot be fixed here, and a referenced assembly is free to
        // keep its own types to itself for reasons that are none of this module's business.
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, context.Compilation.Assembly) ||
            !hubClients.TryGetValue(type, out var hub) ||
            !ModuleFacts.IsInvisibleOutsideAssembly(type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.HubClientTypeMustBeReachable,
            type.Locations.FirstOrDefault() ?? Location.None,
            type.Name,
            hub));
    }

    /// <summary>
    /// The <c>fusion_modules_allowed_public_types</c> list, read once per syntax tree rather than
    /// once per public type: options can differ by directory, but not by type.
    /// </summary>
    private sealed class AllowedPublicTypes(AnalyzerConfigOptionsProvider provider)
    {
        private readonly ConcurrentDictionary<SyntaxTree, HashSet<string>> _byTree = new();

        public bool Allows(INamedTypeSymbol type)
        {
            var tree = type.Locations.FirstOrDefault(l => l.SourceTree is not null)?.SourceTree;
            if (tree is null)
            {
                return false;
            }

            var names = _byTree.GetOrAdd(tree, Read);

            return names.Count > 0 &&
                (names.Contains(type.Name) || names.Contains(type.ToDisplayString()));
        }

        private HashSet<string> Read(SyntaxTree tree)
        {
            var options = provider.GetOptions(tree);
            if (!options.TryGetValue($"build_property.{AllowedPublicTypesOption}", out var configured) &&
                !options.TryGetValue(AllowedPublicTypesOption, out configured))
            {
                return [];
            }

            return new HashSet<string>(
                configured
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Trim())
                    .Where(entry => entry.Length > 0),
                StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Types the framework has to reach by reflection, which therefore cannot be internal.
    /// </summary>
    private sealed class TypeExemptions(Compilation compilation, DeclaredTypes declared)
    {
        private readonly INamedTypeSymbol? _pageModel = compilation.GetTypeByMetadataName(ModuleFacts.PageModelMetadataName);
        private readonly INamedTypeSymbol? _viewComponentAttribute = compilation.GetTypeByMetadataName(ModuleFacts.ViewComponentAttributeMetadataName);
        private readonly INamedTypeSymbol? _tagHelper = compilation.GetTypeByMetadataName(ModuleFacts.TagHelperMetadataName);
        private readonly INamedTypeSymbol? _hub = compilation.GetTypeByMetadataName(ModuleFacts.HubMetadataName);

        public bool IsExempt(INamedTypeSymbol type) =>
            declared.EntityTypes.Contains(type) ||
            // A hub is found by reflection like a controller; its client interface is one MOD0010
            // may require to be public, and two rules must not argue over the same line.
            declared.HubClients.ContainsKey(type) ||
            (_hub is not null && ModuleFacts.InheritsFrom(type, _hub)) ||
            (_pageModel is not null && ModuleFacts.InheritsFrom(type, _pageModel)) ||
            (_tagHelper is not null && ModuleFacts.Implements(type, _tagHelper)) ||
            ModuleFacts.HasAttribute(type, _viewComponentAttribute) ||
            type.Name.EndsWith("ViewComponent", StringComparison.Ordinal);
    }
}
