using Microsoft.Extensions.Configuration;

namespace FusionModules;

/// <summary>
/// How the runtime's settings name a type, in one place.
/// </summary>
/// <remarks>
/// <c>FusionModules:AllowedServiceCollisions</c> takes type names, written by a person, and the
/// startup checks compare them with types they hold. A spelling that is not accepted is a setting
/// that silently does nothing, so the accepted spellings are listed in one place and tested against
/// one list of cases, <c>tests/Shared/TypeSpellingCases.cs</c>, which the analyzers' spellings are
/// tested against too.
/// </remarks>
internal static class TypeNames
{
    /// <summary>Whether any entry names <paramref name="type"/> in one of its <see cref="Spellings"/>.</summary>
    public static bool Matches(Type type, IReadOnlySet<string> entries) =>
        entries.Count > 0 && Spellings(type).Any(entries.Contains);

    /// <summary>
    /// A setting that holds type names: a list of children, a value separated by <c>;</c> or
    /// <c>,</c>, or both (<see cref="TopologyConventions.SplitNames"/>).
    /// </summary>
    public static HashSet<string> ReadList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        return section.GetChildren()
            .Select(child => child.Value)
            .Append(section.Value)
            .SelectMany(TopologyConventions.SplitNames)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The spellings an entry may use: the short and the full name, the way C# writes a generic
    /// (<c>IHandler&lt;Order&gt;</c>, or with the arguments by full name, <c>MyLib.IHandler&lt;Shop.Order&gt;</c>),
    /// and for a generic its definition in the same forms plus the metadata name
    /// (<c>MyLib.IHandler`1</c>), each of which covers every construction. A nested type's full name
    /// is accepted with <c>+</c>, as .NET writes it, and with <c>.</c>, as C# does. Whitespace is
    /// dropped, as <see cref="ReadList"/> drops it from the entries.
    /// </summary>
    public static IEnumerable<string> Spellings(Type type)
    {
        var forms = new List<string> { Readable(type, full: false), Readable(type, full: true), Readable(type, full: true, fullArguments: true) };
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (type.IsGenericType)
        {
            forms.Add(Readable(definition, full: false));
            forms.Add(Readable(definition, full: true));
        }

        // A constructed generic's FullName carries its arguments' assembly names; no entry spells that.
        if (definition.FullName is { } metadataName)
        {
            forms.Add(metadataName);
        }

        return forms
            .SelectMany(form => form.Contains('+') ? new[] { form, form.Replace('+', '.') } : [form])
            .Select(TopologyConventions.WithoutWhitespace)
            .Distinct();
    }

    /// <summary>
    /// A readable name for a type, generics included: <c>IntegrationEventLogService&lt;CatalogContext&gt;</c>;
    /// with <paramref name="fullArguments"/>, the arguments by full name too (<c>Shop.Worker&lt;Shop.Order&gt;</c>).
    /// A definition reads <c>Worker&lt;&gt;</c>. A type nested in a generic one carries the outer type's
    /// arguments on the outer type's name, <c>Shop.Outer&lt;Int32&gt;+Inner</c>, as C# places them.
    /// </summary>
    public static string Readable(Type? type, bool full, bool fullArguments = false)
    {
        if (type is null)
        {
            return "?";
        }

        if (!type.IsGenericType)
        {
            return full ? type.FullName ?? type.Name : type.Name;
        }

        // The arguments of every enclosing generic and then the type's own, in that order, and each
        // name segment of the definition takes as many as its arity says.
        var arguments = type.GetGenericArguments();
        var open = type.IsGenericTypeDefinition;
        var definition = type.GetGenericTypeDefinition();
        var segments = (full ? definition.FullName ?? definition.Name : definition.Name).Split('+');
        var taken = full ? 0 : arguments.Length - Arity(segments[0]);

        return string.Join("+", segments.Select(segment =>
        {
            var arity = Arity(segment);
            if (arity == 0)
            {
                return segment;
            }

            var own = arguments.Skip(taken).Take(arity);
            taken += arity;
            var list = open
                ? new string(',', arity - 1)
                : string.Join(", ", own.Select(argument => Readable(argument, full: fullArguments, fullArguments)));
            return $"{segment[..segment.IndexOf('`')]}<{list}>";
        }));
    }

    /// <summary>The arity a metadata name segment declares: <c>Worker`2</c> is 2.</summary>
    private static int Arity(string segment)
    {
        var tick = segment.IndexOf('`');
        return tick >= 0 && int.TryParse(segment.AsSpan(tick + 1), out var arity) ? arity : 0;
    }
}
