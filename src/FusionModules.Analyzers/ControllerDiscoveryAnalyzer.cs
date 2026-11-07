using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// A module's controllers, Razor pages and views that MVC never finds (MOD0011).
/// </summary>
/// <remarks>
/// <para>
/// MVC finds controllers in its application parts. In a plain web application the SDK makes every
/// referenced MVC project a part, but that is exactly what this package turns off for a host
/// (MOD0007): a part the host adds is in every topology, whether or not the module was activated.
/// So each module adds its own assembly, from its own code — and a module that forgets to answers
/// every route of every controller it has with 404. The application starts and the probe passes.
/// </para>
/// <para>
/// The second way to lose them is the package's other half. MOD0001 makes a module's types internal,
/// and MVC's discovery skips a type that is not public unless the module calls
/// <c>AllowInternalControllers()</c>. Same symptom, same silence.
/// </para>
/// <para>
/// Razor pages and views are found through the same part. A module that has them and no controllers
/// is reported on its module class; one with controllers is reported on those, and the same fix
/// covers its pages.
/// </para>
/// <para>
/// The report is on each controller MVC will not find; the fix writes the line in the module class.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerDiscoveryAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic property naming what is missing, for the code fix.</summary>
    public const string MissingProperty = "Missing";

    /// <summary>No application part for the module's own assembly.</summary>
    public const string MissingApplicationPart = "ApplicationPart";

    /// <summary>A part, but internal controllers and no <c>AllowInternalControllers()</c>.</summary>
    public const string MissingInternalControllers = "InternalControllers";

    /// <summary>
    /// The diagnostic property saying the module has internal controllers and no
    /// <c>AllowInternalControllers()</c>, so the line the fix writes has to end with the call.
    /// </summary>
    public const string AllowInternalProperty = "AllowInternal";

    /// <summary>The diagnostic property naming what the module has, for the call the fix writes.</summary>
    public const string MvcProperty = "Mvc";

    /// <summary>Razor pages, and no controllers: <c>AddRazorPages()</c>.</summary>
    public const string RazorPages = "RazorPages";

    /// <summary>Views, and no controllers: <c>AddControllersWithViews()</c>.</summary>
    public const string Views = "Views";

    private const string RazorPageKind = "mvc.1.0.razor-page";

    private const string ModuleDiscoveryMetadataName = "FusionModules.ModuleDiscovery";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.ControllersNotDiscovered];

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
        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var controllerBase = compilation.GetTypeByMetadataName(ModuleFacts.ControllerBaseMetadataName);
        if (controllerBase is null ||
            !ModuleFacts.IsModuleCompilation(compilation, hostingStartupAttribute, moduleBase, context.CancellationToken))
        {
            return;
        }

        var nonController = compilation.GetTypeByMetadataName(ModuleFacts.NonControllerAttributeMetadataName);

        // What the module calls is a fact about the whole assembly, and the report belongs on each
        // controller, where the code fix is offered as you type. So the calls are found once, by
        // name, the first time a controller asks — and only then: most modules have none.
        var calls = new Lazy<(bool PartAdded, bool InternalAllowed)>(() => FindCalls(compilation, context.CancellationToken));

        // The compiled .cshtml of the module, as the Razor generator records it on the assembly, and
        // whether there are controllers to carry the report instead.
        var razor = new Lazy<(int Count, bool Pages)>(() => CompiledRazor(compilation));
        var controllers = new Lazy<(bool Any, bool AnyInternal)>(() =>
        {
            var found = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, context.CancellationToken)
                .OfType<INamedTypeSymbol>()
                .Where(type => IsController(type, controllerBase, nonController))
                .ToList();
            return (found.Count > 0, found.Any(type => type.DeclaredAccessibility != Accessibility.Public));
        });
        var modules = ModuleFacts.GetHostingStartupTypes(compilation.Assembly, hostingStartupAttribute)
            .Where(type => ModuleFacts.InheritsFrom(type, moduleBase!))
            .ToImmutableHashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        context.RegisterSymbolAction(
            symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (modules.Contains(type))
                {
                    if (razor.Value.Count > 0 && !controllers.Value.Any && !calls.Value.PartAdded)
                    {
                        ReportRazor(symbolContext, type, razor.Value.Count, razor.Value.Pages);
                    }

                    return;
                }

                if (!IsController(type, controllerBase, nonController))
                {
                    return;
                }

                var (partAdded, internalAllowed) = calls.Value;
                if (!partAdded)
                {
                    Report(symbolContext, type, MissingApplicationPart, "MOD0011_NoApplicationPart",
                        allowInternal: !internalAllowed && controllers.Value.AnyInternal);
                }
                else if (type.DeclaredAccessibility != Accessibility.Public && !internalAllowed)
                {
                    Report(symbolContext, type, MissingInternalControllers, "MOD0011_InternalNotAllowed", allowInternal: true);
                }
            },
            SymbolKind.NamedType);
    }

    /// <summary>
    /// Whether the assembly adds an application part and allows internal controllers, by the names
    /// of the calls. Lenient on purpose: which assembly an <c>AddApplicationPart</c> names is not
    /// always visible to the compiler, and a module that manages its parts by hand
    /// (<c>ConfigureApplicationPartManager</c>) has thought about it.
    /// </summary>
    internal static (bool PartAdded, bool InternalAllowed) FindCalls(Compilation compilation, CancellationToken cancellationToken)
    {
        var partAdded = false;
        var internalAllowed = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            // In the IDE this runs again after every edit. A file whose text names none of the calls
            // has none of them, and most files are such files: only those that do are walked.
            var text = tree.GetText(cancellationToken).ToString();
            if (!CallNames.Any(name => text.Contains(name)))
            {
                continue;
            }

            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                switch (InvocationName(invocation))
                {
                    case "AddApplicationPart":
                    case "ConfigureApplicationPartManager":
                        partAdded = true;
                        break;
                    case "AllowInternalControllers":
                        internalAllowed = true;
                        break;
                }

                if (partAdded && internalAllowed)
                {
                    return (true, true);
                }
            }
        }

        return (partAdded, internalAllowed);
    }

    private static readonly string[] CallNames = ["AddApplicationPart", "ConfigureApplicationPartManager", "AllowInternalControllers"];

    /// <summary>How many .cshtml the assembly compiled, and whether any is a Razor page.</summary>
    private static (int Count, bool Pages) CompiledRazor(Compilation compilation)
    {
        var attribute = compilation.GetTypeByMetadataName(ModuleFacts.RazorCompiledItemAttributeMetadataName);
        var items = ModuleFacts.GetAttributes(compilation.Assembly, attribute).ToList();
        var pages = items.Any(item => item.ConstructorArguments.Length > 1 && item.ConstructorArguments[1].Value as string == RazorPageKind);
        return (items.Count, pages);
    }

    /// <summary>The name a call is made by: <c>Name</c> in <c>x.Name(…)</c> and in <c>Name(…)</c>.</summary>
    internal static string? InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => null,
    };

    /// <summary>
    /// What MVC's discovery would take for a controller, for the types a module writes: a top-level,
    /// non-generic, non-abstract class deriving from <c>ControllerBase</c>, not marked
    /// <c>[NonController]</c>. Plain classes that only end in "Controller" are left out; they are
    /// controllers to MVC too, but rare enough in a module that a report would mostly be wrong.
    /// </summary>
    internal static bool IsController(INamedTypeSymbol type, INamedTypeSymbol controllerBase, INamedTypeSymbol? nonController) =>
        type.TypeKind == TypeKind.Class &&
        !type.IsAbstract &&
        type.ContainingType is null &&
        type.TypeParameters.Length == 0 &&
        ModuleFacts.InheritsFrom(type, controllerBase) &&
        !ModuleFacts.HasAttribute(type, nonController);

    private static void Report(SymbolAnalysisContext context, INamedTypeSymbol controller, string missing, string reasonKey, bool allowInternal)
    {
        var reason = Resources.ResourceManager.GetString(reasonKey, CultureInfo.InvariantCulture) ?? reasonKey;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(MissingProperty, missing);
        if (allowInternal)
        {
            properties = properties.Add(AllowInternalProperty, "true");
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.ControllersNotDiscovered,
            controller.Locations[0],
            properties,
            $"controller '{controller.Name}'",
            reason));
    }

    private static void ReportRazor(SymbolAnalysisContext context, INamedTypeSymbol module, int count, bool pages)
    {
        var reason = Resources.ResourceManager.GetString("MOD0011_NoApplicationPart", CultureInfo.InvariantCulture) ?? "MOD0011_NoApplicationPart";
        var what = string.Format(CultureInfo.InvariantCulture, "the {0} compiled into '{1}' ({2})", pages ? "Razor pages" : "views", context.Compilation.AssemblyName, count);

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.ControllersNotDiscovered,
            module.Locations[0],
            ImmutableDictionary<string, string?>.Empty
                .Add(MissingProperty, MissingApplicationPart)
                .Add(MvcProperty, pages ? RazorPages : Views),
            what,
            reason));
    }
}
