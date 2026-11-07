using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// What each module a model is composed from configures.
/// </summary>
internal static class ModuleEntities
{
    /// <summary>A loaded module: its name and the entities it configures.</summary>
    internal sealed record Module(string Name, IReadOnlyCollection<Type> Entities);

    /// <summary>A module of the model as reflection sees it.</summary>
    public static Module Of(Assembly assembly) => new(assembly.GetName().Name ?? string.Empty, ConfiguredEntities(assembly));

    /// <summary>
    /// A type as a reader writes it: <c>IdentityRoleClaim&lt;String&gt;</c> rather than reflection's
    /// <c>IdentityRoleClaim`1</c>.
    /// </summary>
    public static string Readable(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var name = tick < 0 ? type.Name : type.Name[..tick];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Readable)) + ">";
    }

    /// <summary>The entities named by the assembly's <see cref="IEntityTypeConfiguration{TEntity}"/> classes.</summary>
    private static Type[] ConfiguredEntities(Assembly assembly) =>
        [.. LoadableTypes(assembly)
            // What EF Core's ApplyConfigurationsFromAssembly applies: it skips a configuration without a
            // public parameterless constructor, and the line names only what is in the model.
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetInterfaces())
            .Where(@interface => @interface.IsGenericType && @interface.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(@interface => @interface.GetGenericArguments()[0])
            .Distinct()];

    /// <summary>
    /// The assembly's types, or those of them that load: a module with an optional dependency that is
    /// not deployed still has its configurations, and EF Core applies them the same way.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }
}
