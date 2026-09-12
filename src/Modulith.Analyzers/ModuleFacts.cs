using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Modulith.Analyzers;

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

    /// <summary>
    /// Resolves <c>Modulith.ModuleBase</c>. Uses the plural lookup because the type can legitimately
    /// appear more than once — from the package and from a vendored copy, say.
    /// </summary>
    public static INamedTypeSymbol? GetModuleBaseType(Compilation compilation) =>
        compilation.GetTypesByMetadataName(ModulithConstants.ModuleBaseMetadataName).FirstOrDefault();

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
    /// True when the assembly is a Modulith module: it declares a hosting startup whose type
    /// derives from <c>ModuleBase</c>. Third-party hosting startups (Application Insights and
    /// friends) are deliberately excluded — they are not ours to police.
    /// </summary>
    public static bool IsModuleAssembly(IAssemblySymbol assembly, INamedTypeSymbol? hostingStartupAttribute, INamedTypeSymbol? moduleBase) =>
        moduleBase is not null &&
        GetHostingStartupTypes(assembly, hostingStartupAttribute).Any(type => InheritsFrom(type, moduleBase));

    /// <summary>
    /// Entity types configured by an <c>IEntityTypeConfiguration&lt;T&gt;</c> declared in this
    /// assembly. EF Core reaches them by reflection, so they have to stay public.
    /// </summary>
    public static HashSet<ITypeSymbol> GetConfiguredEntityTypes(Compilation compilation)
    {
        var result = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        var configurationInterface = compilation
            .GetTypeByMetadataName(EntityTypeConfigurationMetadataName)
            ?.ConstructUnboundGenericType();

        if (configurationInterface is null)
        {
            return result;
        }

        foreach (var type in GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            foreach (var @interface in type.AllInterfaces)
            {
                if (@interface.IsGenericType &&
                    SymbolEqualityComparer.Default.Equals(@interface.ConstructUnboundGenericType(), configurationInterface))
                {
                    result.Add(@interface.TypeArguments[0]);
                }
            }
        }

        return result;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            foreach (var member in stack.Pop().GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol nested:
                        stack.Push(nested);
                        break;
                    case INamedTypeSymbol type:
                        yield return type;
                        break;
                }
            }
        }
    }
}
