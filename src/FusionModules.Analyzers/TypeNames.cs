using Microsoft.CodeAnalysis;

namespace FusionModules.Analyzers;

/// <summary>
/// How the analyzers name a type, the same ways the runtime does (<c>TypeNames</c> in the
/// FusionModules package): the readable full name a message shows, the identity that tells two
/// namesakes in different assemblies apart, and the spellings an allow-list entry may use.
/// </summary>
internal static class TypeNames
{
    /// <summary>
    /// The spellings an option entry may use for a type — the same set the runtime accepts for its
    /// settings (<c>TypeNames.Spellings</c> in the FusionModules package), so a name written once is
    /// right in <c>.editorconfig</c> and in configuration alike: short and full names, C#-style
    /// generics (<c>Worker&lt;Order&gt;</c>), a generic definition (<c>Worker&lt;&gt;</c>,
    /// <c>Shop.Worker`1</c>), and a nested type's full name with <c>+</c> or <c>.</c>. Both sides
    /// check themselves against one list of cases, <c>tests/Shared/TypeSpellingCases.cs</c>.
    /// </summary>
    public static IEnumerable<string> TypeSpellings(ITypeSymbol type)
    {
        var forms = new List<string> { Readable(type, full: false), Readable(type, full: true), Readable(type, full: true, fullArguments: true) };
        var definition = type.OriginalDefinition;
        if (type is INamedTypeSymbol named && IsGeneric(named))
        {
            forms.Add(Readable(definition, full: false));
            forms.Add(Readable(definition, full: true));
        }

        forms.Add(MetadataFullName(definition));

        return forms
            .SelectMany(form => form.IndexOf('+') >= 0 ? new[] { form, form.Replace('+', '.') } : new[] { form })
            .Select(TopologyConventions.WithoutWhitespace)
            .Distinct();
    }

    /// <summary>
    /// A type's full name with its generic arguments by full name too (<c>Shop.Worker&lt;Shop.Order&gt;</c>):
    /// what a message shows.
    /// </summary>
    public static string FullName(ITypeSymbol type) => TopologyConventions.WithoutWhitespace(Readable(type, full: true, fullArguments: true));

    /// <summary>
    /// A type's full name with the assembly of it and of each generic argument, in the shape of
    /// .NET's <c>AssemblyQualifiedName</c> without versions: two internal types of one name in two
    /// modules are two types, and this tells them apart where <see cref="FullName"/> cannot.
    /// </summary>
    public static string Identity(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => Identity(array.ElementType) + "[]",
        ITypeParameterSymbol parameter => parameter.Name,
        INamedTypeSymbol { IsGenericType: true } named when !named.IsDefinition =>
            $"{MetadataFullName(named.OriginalDefinition)}[{string.Join(",", named.TypeArguments.Select(argument => "[" + Identity(argument) + "]"))}], {type.ContainingAssembly?.Name}",
        _ => $"{MetadataFullName(type)}, {type.ContainingAssembly?.Name}",
    };

    /// <summary>
    /// True for a service type several registrations share by design, as the runtime check has it
    /// (<see cref="TopologyConventions"/>): one of the framework's, EF Core's non-generic
    /// <c>DbContextOptions</c>, or a contract marked with an attribute named
    /// <c>MultipleImplementationsAttribute</c>, in any namespace.
    /// </summary>
    public static bool IsSharedByDesign(ITypeSymbol type) =>
        TopologyConventions.IsFrameworkAssembly(type.ContainingAssembly?.Name) ||
        TopologyConventions.SharedByDesign.Contains(MetadataFullName(type.OriginalDefinition)) ||
        type.OriginalDefinition.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == TopologyConventions.MultipleImplementationsAttributeName);

    /// <summary>The name .NET reflection reports as <c>FullName</c> for a type that is not constructed: <c>Shop.Outer+Worker`1</c>.</summary>
    private static string MetadataFullName(ITypeSymbol type)
    {
        // A type parameter's containing type is the generic that declares it, not an outer class.
        if (type is ITypeParameterSymbol)
        {
            return type.Name;
        }

        if (type.ContainingType is { } outer)
        {
            return MetadataFullName(outer) + "+" + type.MetadataName;
        }

        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? type.MetadataName : ns.ToDisplayString() + "." + type.MetadataName;
    }

    /// <summary>
    /// The runtime's readable name: generic arity dropped, arguments by short name (by full name with
    /// <paramref name="fullArguments"/>), a definition as <c>&lt;&gt;</c>. A type nested in a generic
    /// one carries the outer type's arguments on the outer type's name, <c>Shop.Outer&lt;Int32&gt;+Inner</c>.
    /// </summary>
    private static string Readable(ITypeSymbol type, bool full, bool fullArguments = false)
    {
        if (type is not INamedTypeSymbol named || !IsGeneric(named))
        {
            return full ? MetadataFullName(type) : type.MetadataName;
        }

        // The arguments of every enclosing generic and then the type's own, in that order, and each
        // name segment of the definition takes as many as its arity says — as reflection lists them.
        var arguments = AllTypeArguments(named);
        var open = named.IsDefinition;
        var segments = (full ? MetadataFullName(named.OriginalDefinition) : named.OriginalDefinition.MetadataName).Split('+');
        var taken = full ? 0 : arguments.Count - named.Arity;

        return string.Join("+", segments.Select(segment =>
        {
            var tick = segment.IndexOf('`');
            if (tick < 0 || !int.TryParse(segment.Substring(tick + 1), out var arity) || arity == 0)
            {
                return segment;
            }

            var own = arguments.Skip(taken).Take(arity);
            taken += arity;
            var list = open
                ? new string(',', arity - 1)
                : string.Join(", ", own.Select(argument => Readable(argument, full: fullArguments, fullArguments)));
            return $"{segment.Substring(0, tick)}<{list}>";
        }));
    }

    /// <summary>Whether the type or any type it is nested in has type parameters.</summary>
    private static bool IsGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The type arguments of the enclosing types, outermost first, then the type's own.</summary>
    private static List<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
    {
        var arguments = type.ContainingType is { } outer ? AllTypeArguments(outer) : [];
        arguments.AddRange(type.TypeArguments);
        return arguments;
    }
}
