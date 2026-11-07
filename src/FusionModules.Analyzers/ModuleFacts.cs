using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// The handful of questions every rule needs to ask about a compilation.
/// </summary>
internal static class ModuleFacts
{
    public const string HostingStartupAttributeMetadataName = "Microsoft.AspNetCore.Hosting.HostingStartupAttribute";
    public const string ApplicationPartAttributeMetadataName = "Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartAttribute";
    public const string ControllerBaseMetadataName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    public const string PageModelMetadataName = "Microsoft.AspNetCore.Mvc.RazorPages.PageModel";
    public const string ViewComponentAttributeMetadataName = "Microsoft.AspNetCore.Mvc.ViewComponentAttribute";
    public const string TagHelperMetadataName = "Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper";
    public const string EntityTypeConfigurationMetadataName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";
    public const string ModuleBaseMetadataName = "FusionModules.ModuleBase";
    public const string HubMetadataName = "Microsoft.AspNetCore.SignalR.Hub";
    public const string HubOfTMetadataName = "Microsoft.AspNetCore.SignalR.Hub`1";
    public const string InternalsVisibleToAttributeMetadataName =
        "System.Runtime.CompilerServices.InternalsVisibleToAttribute";

    /// <summary>
    /// The assembly SignalR emits a strongly-typed hub client proxy into. It is built with
    /// <c>AssemblyBuilderAccess.Run</c> and is therefore unsigned, so naming it in
    /// <c>InternalsVisibleTo</c> is enough to let the proxy implement an internal interface.
    /// </summary>
    public const string TypedClientBuilderAssemblyName = "Microsoft.AspNetCore.SignalR.TypedClientBuilder";

    /// <summary>Set by the package's targets; see <c>FusionModules.targets</c>.</summary>
    public const string ProjectKindOption = "build_property.FusionModulesProjectKind";

    public const string HostApplicationBuilderMetadataName = "Microsoft.Extensions.Hosting.IHostApplicationBuilder";
    public const string HostBuilderMetadataName = "Microsoft.Extensions.Hosting.IHostBuilder";
    public const string WebHostDefaultsMetadataName = "Microsoft.AspNetCore.Hosting.WebHostDefaults";
    public const string WebApplicationMetadataName = "Microsoft.AspNetCore.Builder.WebApplication";
    public const string ApplicationBuilderMetadataName = "Microsoft.AspNetCore.Builder.IApplicationBuilder";
    public const string HostingStartupMetadataName = "Microsoft.AspNetCore.Hosting.IHostingStartup";
    public const string StartupFilterMetadataName = "Microsoft.AspNetCore.Hosting.IStartupFilter";
    public const string WebHostBuilderMetadataName = "Microsoft.AspNetCore.Hosting.IWebHostBuilder";
    public const string WebHostEnvironmentMetadataName = "Microsoft.AspNetCore.Hosting.IWebHostEnvironment";
    public const string MvcBuilderMetadataName = "Microsoft.Extensions.DependencyInjection.IMvcBuilder";
    public const string NonControllerAttributeMetadataName = "Microsoft.AspNetCore.Mvc.NonControllerAttribute";
    public const string RazorCompiledItemAttributeMetadataName = "Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute";

    /// <summary>The one <c>WebApplication</c> factory that runs hosting startups.</summary>
    public const string CreateBuilderMethodName = "CreateBuilder";

    /// <summary>
    /// The class <c>KnownModulesGenerator</c> writes <see cref="ValidateModulesMethodName"/> into,
    /// and the namespace it puts it in: the builder's own, so that no using is needed.
    /// </summary>
    public const string ValidateModulesNamespace = "Microsoft.Extensions.Hosting";
    public const string ValidateModulesClassName = "FusionModulesHostApplicationBuilderExtensions";
    public const string ValidateModulesClassMetadataName = ValidateModulesNamespace + "." + ValidateModulesClassName;
    public const string ValidateModulesMethodName = "ValidateModules";

    /// <summary>
    /// Resolves <c>FusionModules.ModuleBase</c>. Uses the plural lookup because the type can legitimately
    /// appear more than once — from the package and from a vendored copy, say.
    /// </summary>
    public static INamedTypeSymbol? GetModuleBaseType(Compilation compilation) =>
        compilation.GetTypesByMetadataName(ModuleBaseMetadataName).FirstOrDefault();

    public static bool InheritsFrom(ITypeSymbol? symbol, ITypeSymbol baseType)
    {
        for (var current = symbol?.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Implements(ITypeSymbol symbol, ITypeSymbol @interface) =>
        symbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, @interface));

    public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType) =>
        attributeType is not null &&
        symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

    public static IEnumerable<AttributeData> GetAttributes(ISymbol symbol, INamedTypeSymbol? attributeType) =>
        attributeType is null
            ? []
            : symbol.GetAttributes().Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

    /// <summary>
    /// The types named by <c>[assembly: HostingStartup(typeof(X))]</c>. The attribute allows
    /// multiple, so an assembly may carry several modules.
    /// </summary>
    public static IEnumerable<ITypeSymbol> GetHostingStartupTypes(IAssemblySymbol assembly, INamedTypeSymbol? hostingStartupAttribute)
    {
        foreach (var attribute in GetAttributes(assembly, hostingStartupAttribute))
        {
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is ITypeSymbol type)
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// True when the assembly is a FusionModules module: it declares a hosting startup whose type
    /// derives from <c>ModuleBase</c>. Third-party hosting startups (Application Insights and
    /// friends) are deliberately excluded — they are not ours to police.
    /// </summary>
    public static bool IsModuleAssembly(IAssemblySymbol assembly, INamedTypeSymbol? hostingStartupAttribute, INamedTypeSymbol? moduleBase) =>
        moduleBase is not null &&
        GetHostingStartupTypes(assembly, hostingStartupAttribute).Any(type => InheritsFrom(type, moduleBase));

    /// <summary>
    /// True when the compilation itself is a module: a library that declares a module. The host's
    /// own executable may declare one too, and is still held to the host's rules, not a module's.
    /// </summary>
    public static bool IsModuleCompilation(
        Compilation compilation,
        INamedTypeSymbol? hostingStartupAttribute,
        INamedTypeSymbol? moduleBase,
        CancellationToken cancellationToken) =>
        hostingStartupAttribute is not null &&
        moduleBase is not null &&
        compilation.GetEntryPoint(cancellationToken) is null &&
        IsModuleAssembly(compilation.Assembly, hostingStartupAttribute, moduleBase);

    /// <summary>
    /// The modules among the assemblies this compilation references.
    /// </summary>
    public static IEnumerable<IAssemblySymbol> GetReferencedModules(
        Compilation compilation,
        INamedTypeSymbol? hostingStartupAttribute,
        INamedTypeSymbol? moduleBase) =>
        compilation.SourceModule.ReferencedAssemblySymbols
            .Where(assembly => IsModuleAssembly(assembly, hostingStartupAttribute, moduleBase));

    /// <summary>Whether the project is a host; see <see cref="IsHost(ProjectKind, Compilation, CancellationToken)"/>.</summary>
    public static bool IsHost(Compilation compilation, AnalyzerConfigOptions globalOptions, CancellationToken cancellationToken) =>
        IsHost(GetProjectKind(globalOptions), compilation, cancellationToken);

    /// <summary>
    /// Whether the project is a host: what MSBuild classified as one, or — without the package's
    /// targets, so without a classification — a program. A test project has an entry point too and
    /// is never a host; neither is a project set to <c>Module</c>, entry point or not.
    /// </summary>
    public static bool IsHost(ProjectKind kind, Compilation compilation, CancellationToken cancellationToken) => kind switch
    {
        ProjectKind.Host => true,
        ProjectKind.Unknown => compilation.GetEntryPoint(cancellationToken) is not null,
        _ => false,
    };

    /// <summary>
    /// The project's kind as MSBuild classified it (<c>FusionModulesProjectKind</c>), or
    /// <see cref="ProjectKind.Unknown"/> without the package's targets. MSBuild compares property
    /// values without regard to case, so this does too.
    /// </summary>
    public static ProjectKind GetProjectKind(AnalyzerConfigOptions globalOptions)
    {
        globalOptions.TryGetValue(ProjectKindOption, out var kind);

        return kind?.Trim().ToUpperInvariant() switch
        {
            "HOST" => ProjectKind.Host,
            "TEST" => ProjectKind.Test,
            "MODULE" => ProjectKind.Module,
            _ => ProjectKind.Unknown,
        };
    }

    /// <summary>
    /// What this assembly's own types say about which of them the framework reaches by reflection,
    /// gathered in one walk over every type, nested ones included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entity types configured by an <c>IEntityTypeConfiguration&lt;T&gt;</c>: EF Core reaches them
    /// by reflection, so they have to stay public.
    /// </para>
    /// <para>
    /// Client interfaces named by a <c>Hub&lt;TClient&gt;</c>, each mapped to the name of the hub that
    /// names it. Keyed by the client rather than the hub: two hubs may share one client interface,
    /// and the interface is the thing a rule reports on. The first hub found supplies the name,
    /// because a message needs one and not a list.
    /// </para>
    /// </remarks>
    public static DeclaredTypes ScanDeclaredTypes(Compilation compilation)
    {
        var entityTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var hubClients = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);

        var configurationInterface = compilation
            .GetTypeByMetadataName(EntityTypeConfigurationMetadataName)
            ?.ConstructUnboundGenericType();
        var hubOfT = compilation.GetTypeByMetadataName(HubOfTMetadataName)?.ConstructUnboundGenericType();

        if (configurationInterface is null && hubOfT is null)
        {
            return new DeclaredTypes(entityTypes, hubClients);
        }

        foreach (var type in GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            if (configurationInterface is not null)
            {
                foreach (var @interface in type.AllInterfaces)
                {
                    if (@interface.IsGenericType &&
                        SymbolEqualityComparer.Default.Equals(@interface.ConstructUnboundGenericType(), configurationInterface))
                    {
                        entityTypes.Add(@interface.TypeArguments[0]);
                    }
                }
            }

            if (hubOfT is null)
            {
                continue;
            }

            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType &&
                    SymbolEqualityComparer.Default.Equals(current.ConstructUnboundGenericType(), hubOfT))
                {
                    if (!hubClients.ContainsKey(current.TypeArguments[0]))
                    {
                        hubClients[current.TypeArguments[0]] = type.Name;
                    }

                    break;
                }
            }
        }

        return new DeclaredTypes(entityTypes, hubClients);
    }

    /// <summary>
    /// True when the assembly grants <paramref name="friendAssemblyName"/> access to its internals.
    /// </summary>
    /// <remarks>
    /// The attribute argument may carry a public key (<c>"Name, PublicKey=…"</c>), so only the
    /// simple name is compared.
    /// </remarks>
    public static bool HasInternalsVisibleTo(Compilation compilation, string friendAssemblyName)
    {
        var attributeType = compilation.GetTypeByMetadataName(InternalsVisibleToAttributeMetadataName);

        foreach (var attribute in GetAttributes(compilation.Assembly, attributeType))
        {
            if (attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Value is not string value)
            {
                continue;
            }

            var comma = value.IndexOf(',');
            var name = comma < 0 ? value : value.Substring(0, comma);

            if (name.Trim().Equals(friendAssemblyName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when nothing outside this assembly can see the type — which is what a proxy in another
    /// assembly cares about, and not the same question as the type's own declared accessibility:
    /// a public type nested in an internal one is invisible too.
    /// </summary>
    public static bool IsInvisibleOutsideAssembly(ITypeSymbol type)
    {
        for (ISymbol? current = type; current is INamedTypeSymbol named; current = named.ContainingType)
        {
            if (named.DeclaredAccessibility != Accessibility.Public)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INamedTypeSymbol type)
            {
                yield return type;

                foreach (var nested in type.GetTypeMembers())
                {
                    stack.Push(nested);
                }

                continue;
            }

            foreach (var member in ((INamespaceSymbol)current).GetMembers())
            {
                stack.Push(member);
            }
        }
    }
}

/// <summary>The result of <see cref="ModuleFacts.ScanDeclaredTypes"/>.</summary>
internal sealed class DeclaredTypes(HashSet<ITypeSymbol> entityTypes, Dictionary<ITypeSymbol, string> hubClients)
{
    /// <summary>Entity types configured by an <c>IEntityTypeConfiguration&lt;T&gt;</c> in this assembly.</summary>
    public HashSet<ITypeSymbol> EntityTypes { get; } = entityTypes;

    /// <summary>Hub client interfaces, each with the name of a hub that names it.</summary>
    public Dictionary<ITypeSymbol, string> HubClients { get; } = hubClients;
}

/// <summary>How MSBuild classified the project.</summary>
internal enum ProjectKind
{
    /// <summary>No classification: the package's targets did not run.</summary>
    Unknown,

    /// <summary>An executable that composes modules.</summary>
    Host,

    /// <summary>A test project, which has an entry point and may use module types.</summary>
    Test,

    /// <summary>A library: a module or something a module uses.</summary>
    Module,
}
