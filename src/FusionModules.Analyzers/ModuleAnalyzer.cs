using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// Rules that apply to a module assembly: it keeps its types to itself (MOD0001), the type it
/// names in <c>[assembly: HostingStartup]</c> really is a module (MOD0002), and every module it
/// declares is actually registered (MOD0005).
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

        // MOD0002 / MOD0020 — check what the assembly declares.
        if (declaredStartups.Length > 0)
        {
            RegisterHostingStartupAttributeCheck(context, hostingStartupAttribute, moduleBase);
        }

        if (moduleBase is null)
        {
            return;
        }

        // MOD0005 — every module type must be named by an attribute.
        var registered = new HashSet<ISymbol>(declaredStartups, SymbolEqualityComparer.Default);
        context.RegisterSymbolAction(
            symbolContext => CheckModuleIsRegistered(symbolContext, moduleBase, registered),
            SymbolKind.NamedType);

        // MOD0001 — only applies to a module assembly, and never to the host's own executable.
        if (compilation.GetEntryPoint(context.CancellationToken) is not null ||
            !ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartupAttribute, moduleBase))
        {
            return;
        }

        var hubClients = ModuleFacts.GetHubClients(compilation);

        var exemptions = TypeExemptions.Create(compilation, hubClients);
        context.RegisterSymbolAction(
            symbolContext => CheckTypeIsNotPublic(symbolContext, exemptions),
            SymbolKind.NamedType);

        // MOD0009 — the mirror image: these types the runtime insists on reaching, and an
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

    private static void CheckTypeIsNotPublic(SymbolAnalysisContext context, TypeExemptions exemptions)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.DeclaredAccessibility != Accessibility.Public ||
            type.ContainingType is not null ||
            !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, context.Compilation.Assembly) ||
            exemptions.IsExempt(type) ||
            IsAllowedByConfiguration(context, type))
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

    private static bool IsAllowedByConfiguration(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        var tree = type.Locations.FirstOrDefault(l => l.SourceTree is not null)?.SourceTree;
        if (tree is null)
        {
            return false;
        }

        if (!context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)
                .TryGetValue($"build_property.{AllowedPublicTypesOption}", out var configured) &&
            !context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)
                .TryGetValue(AllowedPublicTypesOption, out configured))
        {
            return false;
        }

        var name = type.Name;
        var fullName = type.ToDisplayString();

        return configured
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim())
            .Any(entry => entry.Equals(name, StringComparison.Ordinal) || entry.Equals(fullName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Types the framework has to reach by reflection, which therefore cannot be internal.
    /// </summary>
    private sealed class TypeExemptions
    {
        private readonly INamedTypeSymbol? _controllerBase;
        private readonly INamedTypeSymbol? _pageModel;
        private readonly INamedTypeSymbol? _viewComponentAttribute;
        private readonly INamedTypeSymbol? _tagHelper;
        private readonly INamedTypeSymbol? _hub;
        private readonly HashSet<ITypeSymbol> _entityTypes;
        private readonly Dictionary<ITypeSymbol, string> _hubClients;

        private TypeExemptions(Compilation compilation, Dictionary<ITypeSymbol, string> hubClients)
        {
            _hub = compilation.GetTypeByMetadataName(ModuleFacts.HubMetadataName);
            _hubClients = hubClients;
            _controllerBase = compilation.GetTypeByMetadataName(ModuleFacts.ControllerBaseMetadataName);
            _pageModel = compilation.GetTypeByMetadataName(ModuleFacts.PageModelMetadataName);
            _viewComponentAttribute = compilation.GetTypeByMetadataName(ModuleFacts.ViewComponentAttributeMetadataName);
            _tagHelper = compilation.GetTypeByMetadataName(ModuleFacts.TagHelperMetadataName);
            _entityTypes = ModuleFacts.GetConfiguredEntityTypes(compilation);
        }

        public static TypeExemptions Create(Compilation compilation, Dictionary<ITypeSymbol, string> hubClients) =>
            new(compilation, hubClients);

        public bool IsExempt(INamedTypeSymbol type) =>
            _entityTypes.Contains(type) ||
            // A hub is found by reflection like a controller; its client interface is one MOD0009
            // may require to be public, and two rules must not argue over the same line.
            _hubClients.ContainsKey(type) ||
            (_hub is not null && ModuleFacts.InheritsFrom(type, _hub)) ||
            (_controllerBase is not null && ModuleFacts.InheritsFrom(type, _controllerBase)) ||
            (_pageModel is not null && ModuleFacts.InheritsFrom(type, _pageModel)) ||
            (_tagHelper is not null && ModuleFacts.Implements(type, _tagHelper)) ||
            ModuleFacts.HasAttribute(type, _viewComponentAttribute) ||
            type.Name.EndsWith("ViewComponent", StringComparison.Ordinal);
    }
}
