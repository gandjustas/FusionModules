using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// MOD0017 in the model, where a context asks for it (<c>CheckModuleTableOwners</c>): applies each
/// module's configurations in turn and notes which module names which entity's table; when EF Core
/// finalizes the model, refuses it if a table or an entity has two owning modules.
/// </summary>
/// <remarks>
/// <para>
/// The build's rule sees only <c>ToTable</c> with constant names. The model sees the rest: a table
/// named by convention, a name read from configuration, the schema a table ends up in. Two moments,
/// two questions. Who named a table can only be read while the model grows: the table annotations of
/// every entity type before and after each module's configurations, and a change to an explicitly
/// configured name or schema is that module naming the table. Which table that is can only be read
/// once the model is complete: the context may configure more after <c>ApplyModuleConfigurations</c>
/// — a default schema, a mapping strategy — and the relational conventions finish their work when
/// the model is finalized. So the owners are recorded here, against the model, and compared by a
/// model-finalizing convention, the last of them, with the tables the model ends up with. <c>[Table]</c> is its class's module's. An entity nobody names
/// belongs, by convention, to the first module that configures it — the module the others build on,
/// since each module is applied after the modules it references.
/// </para>
/// <para>
/// A derived type of a hierarchy and an owned type live in their root's or owner's table unless they
/// are named themselves, and own nothing: a module may extend another's hierarchy or configure a
/// relationship without owning a table.
/// </para>
/// <para>
/// Table names come from EF Core's relational extensions, called by reflection: this package depends
/// on EF Core alone, and a model without a relational provider in the application has no tables to
/// compare.
/// </para>
/// </remarks>
internal static class ModuleTables
{
    private const string TableNameAnnotation = "Relational:TableName";
    private const string SchemaAnnotation = "Relational:Schema";

    /// <summary>A module of the model: its name, its assembly, the entities its configuration classes name, and how it configures the model.</summary>
    internal sealed record Contribution(string Name, Assembly? Assembly, IReadOnlyCollection<Type> Entities, Action<ModelBuilder> Apply);

    /// <summary>What <see cref="Apply"/> noted about a model, for <see cref="Check"/> to compare once it is complete.</summary>
    private sealed record Owners(string Context, IReadOnlyList<Contribution> Modules, Dictionary<string, List<(string Module, string Table)>> Named);

    private static readonly ConditionalWeakTable<IReadOnlyModel, Owners> Recorded = new();

    private static readonly Lazy<Func<IReadOnlyEntityType, string?>?> TableName = new(() => Relational("GetTableName"));
    private static readonly Lazy<Func<IReadOnlyEntityType, string?>?> Schema = new(() => Relational("GetSchema"));

    /// <summary>
    /// Applies the modules' configurations in order and, when <paramref name="record"/> is set and the
    /// application has a relational provider, notes which module names which entity's table, for
    /// <see cref="Check"/>.
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder, IReadOnlyList<Contribution> modules, string context, bool record)
    {
        if (!record || TableName.Value is null || Schema.Value is null)
        {
            foreach (var module in modules)
            {
                module.Apply(modelBuilder);
            }

            return;
        }

        // Entity type name → the modules that named its table, with the table each named.
        var named = new Dictionary<string, List<(string Module, string Table)>>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var before = modelBuilder.Model.GetEntityTypes().ToDictionary(type => type.Name, Read, StringComparer.Ordinal);
            module.Apply(modelBuilder);

            foreach (var type in modelBuilder.Model.GetEntityTypes())
            {
                var now = Read(type);
                if (now.IsExplicit && (!before.TryGetValue(type.Name, out var was) || was != now))
                {
                    if (!named.TryGetValue(type.Name, out var namers))
                    {
                        named[type.Name] = namers = [];
                    }

                    namers.RemoveAll(namer => namer.Module == module.Name);
                    namers.Add((module.Name, Display(Schema.Value(type), TableName.Value(type) ?? string.Empty)));
                }
            }
        }

        Recorded.AddOrUpdate(modelBuilder.Model, new Owners(context, modules, named));
    }

    /// <summary>
    /// Fails when two modules own one table or one entity of a model <see cref="Apply"/> recorded,
    /// judged by the tables the model ends up with. Nothing for a model it did not record.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two modules own one table or one entity.</exception>
    public static void Check(IReadOnlyModel model)
    {
        if (!Recorded.TryGetValue(model, out var owners))
        {
            return;
        }

        Recorded.Remove(model);
        var problems = Problems(model, owners.Modules, owners.Named);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(Describe(owners.Context, problems));
        }
    }

    /// <summary>What a module's configuration can change about an entity's table: the name and the schema, each with how it was set.</summary>
    private readonly record struct Mapping(object? Table, ConfigurationSource? TableSource, object? Schema, ConfigurationSource? SchemaSource)
    {
        public bool IsExplicit => TableSource == ConfigurationSource.Explicit || SchemaSource == ConfigurationSource.Explicit;
    }

    private static Mapping Read(IMutableEntityType type)
    {
        var annotatable = (IConventionAnnotatable)type;
        var table = annotatable.FindAnnotation(TableNameAnnotation);
        var schema = annotatable.FindAnnotation(SchemaAnnotation);
        return new Mapping(table?.Value, table?.GetConfigurationSource(), schema?.Value, schema?.GetConfigurationSource());
    }

    private static List<string> Problems(IReadOnlyModel model, IReadOnlyList<Contribution> modules, Dictionary<string, List<(string Module, string Table)>> named)
    {
        var problems = new List<string>();
        var owned = new List<(string Key, string Table, string Entity, string Module, bool ByConvention)>();

        foreach (var type in model.GetEntityTypes())
        {
            var table = TableName.Value!(type);
            if (table is null)
            {
                // A view, a query, a function: not a table anyone owns.
                continue;
            }

            var name = Display(Schema.Value!(type), table);
            var entity = type.HasSharedClrType ? type.Name : type.ClrType.FullName ?? type.Name;

            var namers = named.TryGetValue(type.Name, out var found) ? found.Select(namer => namer.Module).ToList() : [];
            if (!type.HasSharedClrType &&
                type.ClrType.GetCustomAttribute<TableAttribute>(inherit: false) is not null &&
                modules.FirstOrDefault(module => module.Assembly is not null && module.Assembly == type.ClrType.Assembly) is { } declaring &&
                !namers.Contains(declaring.Name))
            {
                namers.Add(declaring.Name);
            }

            if (namers.Count > 1)
            {
                var tables = found ?? [];
                problems.Add(
                    $"'{entity}' is given its table by {Join(namers.Order(StringComparer.Ordinal))}; EF Core keeps the one applied last, '{name}'" +
                    (tables.Count > 1 ? $" ({string.Join(", ", tables.Select(namer => $"{namer.Module}: '{namer.Table}'"))})." : "."));
            }

            if (namers.Count > 0)
            {
                owned.AddRange(namers.Select(module => (Key(name), name, entity, module, false)));
            }
            else if (type.BaseType is null && !type.IsOwned() &&
                     modules.FirstOrDefault(module => !type.HasSharedClrType && module.Entities.Contains(type.ClrType)) is { } configuring)
            {
                owned.Add((Key(name), name, entity, configuring.Name, true));
            }
        }

        foreach (var table in owned.GroupBy(entry => entry.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var owners = table.Select(entry => entry.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            // One entity that two modules name: said above, as the entity.
            if (owners.Count < 2 || table.Select(entry => entry.Entity).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }

            var name = table.Select(entry => entry.Table).Order(StringComparer.Ordinal).First();
            problems.Add($"'{name}' is mapped by {Join(owners.Select(owner => $"{owner} ({string.Join(", ", table.Where(entry => entry.Module == owner).Select(entry => entry.ByConvention ? entry.Entity + ", by convention" : entry.Entity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))})"))}.");
        }

        return problems;
    }

    private static string Describe(string context, List<string> problems)
    {
        var text = new StringBuilder()
            .Append("The model of ").Append(context)
            .Append(" maps a table or an entity from two modules, and a table and an entity have one owning module, the one that names the table (MOD0017):");

        foreach (var problem in problems)
        {
            text.Append("\n- ").Append(problem);
        }

        return text
            .Append("\nPick the owner. The other module reads the data through the owner's contract; or the entity moves to the owner, and the other module ")
            .Append("references it and configures what it adds without naming the table; or the other module reads it through a view (ToView) or a query (ToSqlQuery). ")
            .Append("If these modules are alternatives that no topology should load together, remove one of them from HOSTINGSTARTUPASSEMBLIES.")
            .ToString();
    }

    /// <summary>A table as a reader writes it: <c>Sales.Orders</c>, or <c>Orders</c> without a schema.</summary>
    private static string Display(string? schema, string table) => string.IsNullOrEmpty(schema) ? table : schema + "." + table;

    /// <summary>
    /// What tables are compared by: name and schema without regard to case, as the build's rule does.
    /// The schema here is the one the model gives the table, its default schema included.
    /// </summary>
    private static string Key(string display) => display.ToUpperInvariant();

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count < 2 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    /// <summary>
    /// <c>RelationalEntityTypeExtensions.GetTableName</c> or <c>GetSchema</c>, or null when the
    /// application has no relational provider to load them from.
    /// </summary>
    private static Func<IReadOnlyEntityType, string?>? Relational(string method)
    {
        var extensions = Type.GetType("Microsoft.EntityFrameworkCore.RelationalEntityTypeExtensions, Microsoft.EntityFrameworkCore.Relational", throwOnError: false);
        return extensions?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, [typeof(IReadOnlyEntityType)]) is { } found && found.ReturnType == typeof(string)
            ? found.CreateDelegate<Func<IReadOnlyEntityType, string?>>()
            : null;
    }
}
