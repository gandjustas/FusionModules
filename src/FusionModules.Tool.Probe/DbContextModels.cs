using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tool.Probe;

/// <summary>
/// The model of a DbContext as EF Core itself describes it: the debug view of every entity type —
/// its table, properties, keys, indexes, relationships and their annotations — with its seed data
/// and query filters, which the debug view leaves out. Read by name, as everything of EF Core is
/// here; building a model connects to nothing.
/// </summary>
/// <remarks>
/// What a refactoring of <c>OnModelCreating</c> must leave as it was, and what two services' contexts
/// have to agree on before one host composes both. The design-time model is the one read: the
/// model a context runs with has its seed data and some annotations removed.
/// </remarks>
internal static partial class DbContextModels
{
    private const string Metadata = "Microsoft.EntityFrameworkCore.Metadata.";

    /// <summary>Reads the model of the context the container gives, or says why it could not.</summary>
    public static void Read(Type contextType, IServiceProvider services, ProbeDbContext context)
    {
        try
        {
            using var scope = services.CreateScope();
            if (Create(contextType, scope.ServiceProvider) is not { } instance)
            {
                return;
            }

            try
            {
                context.Model = Describe(instance);
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }
        catch (Exception exception)
        {
            var error = ProbeError.From("dbcontext", exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
            context.ModelError = $"{error.Type}: {error.Message}";
        }
    }

    /// <summary>The context itself, or one from its factory when only a factory is registered.</summary>
    private static object? Create(Type contextType, IServiceProvider services)
    {
        if (services.GetService(contextType) is { } instance)
        {
            return instance;
        }

        var factoryType = contextType.BaseType is null
            ? null
            : FindType(contextType, "Microsoft.EntityFrameworkCore.IDbContextFactory`1")?.MakeGenericType(contextType);
        return factoryType is not null && services.GetService(factoryType) is { } factory
            ? factoryType.GetMethod("CreateDbContext", Type.EmptyTypes)?.Invoke(factory, null)
            : null;
    }

    /// <summary>The model of a context, or null for something that only derives from a type of that name.</summary>
    internal static ProbeDbModel? Describe(object context)
    {
        // context.GetService<IDesignTimeModel>().Model, and the runtime model where EF Core has no such service.
        var model = context.GetType().GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure`1" &&
                                        type.GetGenericArguments()[0] == typeof(IServiceProvider))
                ?.GetProperty("Instance")?.GetValue(context) is IServiceProvider internalServices &&
            FindType(context.GetType(), Metadata + "IDesignTimeModel") is { } designTime &&
            internalServices.GetService(designTime) is { } service
                ? designTime.GetProperty("Model")?.GetValue(service)
                : null;
        model ??= context.GetType().GetProperty("Model")?.GetValue(context);
        if (model is null ||
            Call(model, "ToDebugString", LongDefault(model), 0) is not string view ||
            Call(model, "GetEntityTypes") is not IEnumerable entityTypes)
        {
            return null;
        }

        var described = new ProbeDbModel { Lines = ModelLevel(view) };
        foreach (var entityType in entityTypes)
        {
            if (entityType is null || Property(entityType, "Name") is not string name)
            {
                continue;
            }

            var lines = Call(entityType, "ToDebugString", LongDefault(model), 0) is string entityView
                ? Lines(entityView)
                : [];
            lines.AddRange(QueryFilters(entityType));
            lines.AddRange(SeedData(entityType));
            described.Entities.Add(new ProbeDbEntity { Name = name, Lines = lines });
        }

        described.Entities.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return described;
    }

    /// <summary>
    /// What the model's debug view says outside its entity types: the model's own annotations —
    /// a default schema, sequences, a collation. The version of EF Core that built it is not the model.
    /// </summary>
    private static List<string> ModelLevel(string view)
    {
        var lines = new List<string>();
        var inEntity = false;
        foreach (var line in Lines(view))
        {
            if (line.StartsWith("  EntityType:", StringComparison.Ordinal))
            {
                inEntity = true;
            }
            else if (line.Length > 0 && (line[0] != ' ' || (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ' && !inEntity)))
            {
                inEntity &= line[0] == ' ';
                if (line.TrimEnd() != "Model:" && !line.TrimStart().StartsWith("ProductVersion:", StringComparison.Ordinal))
                {
                    lines.Add(line);
                }
            }
            else if (!inEntity && line.Trim().Length > 0 && !line.TrimStart().StartsWith("ProductVersion:", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        // A heading with nothing under it says nothing.
        return lines.Count == 1 && lines[0].TrimEnd().EndsWith(':') ? [] : lines;
    }

    private static IEnumerable<string> QueryFilters(object entityType)
    {
        // EF Core 10 names filters; before it an entity had one.
        if (Call(entityType, "GetDeclaredQueryFilters") is IEnumerable filters)
        {
            foreach (var filter in filters)
            {
                var key = filter?.GetType().GetProperty("Key")?.GetValue(filter) as string;
                if (filter?.GetType().GetProperty("Expression")?.GetValue(filter) is { } expression)
                {
                    yield return $"  QueryFilter{(string.IsNullOrEmpty(key) ? "" : " " + key)}: {Stable(expression.ToString()!)}";
                }
            }
        }
        else if (Call(entityType, "GetQueryFilter") is { } single)
        {
            yield return $"  QueryFilter: {Stable(single.ToString()!)}";
        }
    }

    private static IEnumerable<string> SeedData(object entityType)
    {
        if (Call(entityType, "GetSeedData", false) is not IEnumerable rows)
        {
            yield break;
        }

        var seeds = new List<string>();
        foreach (var row in rows)
        {
            if (row is IEnumerable<KeyValuePair<string, object?>> values)
            {
                seeds.Add("  Seed: " + string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={Value(pair.Value)}")));
            }
        }

        seeds.Sort(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            yield return seed;
        }
    }

    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable sequence => "[" + string.Join(", ", sequence.Cast<object?>().Select(Value)) + "]",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// An expression's text without the numbers the compiler gives closures and lambdas: a filter
    /// that moves from the context into a configuration class is the same filter.
    /// </summary>
    private static string Stable(string expression) => CompilerNumbers().Replace(expression, "$1");

    [GeneratedRegex(@"(<>c__DisplayClass|<>c|b__)\d+(_\d+)?")]
    private static partial Regex CompilerNumbers();

    /// <summary>
    /// A debug view's lines, without what is not the model: the bookkeeping the conventions leave on
    /// it while it is built, which the view prints as the name of a collection's type; and a schema
    /// set to nothing, which is what <c>ToTable("Orders")</c> leaves — the table is in the model's
    /// default schema with it as without it.
    /// </summary>
    private static List<string> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0 && !Bookkeeping().IsMatch(line)).ToList();

        // An "Annotations:" heading whose every annotation was bookkeeping heads nothing.
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart() == "Annotations:" && (i == lines.Count - 1 || Indent(lines[i + 1]) <= Indent(lines[i])))
            {
                lines.RemoveAt(i);
            }
        }

        return lines;

        static int Indent(string line) => line.Length - line.TrimStart().Length;
    }

    [GeneratedRegex(@"^\s+(\w*Convention\w*(:\w+)?|QueryFilter): [\w.]+(`\d+\[.*\])?$|^\s+\w*ConventionState\w*: |^\s+Relational:Schema:$")]
    private static partial Regex Bookkeeping();

    private static object? LongDefault(object model) =>
        FindType(model.GetType(), "Microsoft.EntityFrameworkCore.Infrastructure.MetadataDebugStringOptions") is { } options
            ? Enum.Parse(options, "LongDefault")
            : null;

    /// <summary>A type of the EF Core the object comes from: its own assembly, or one an interface of its is declared in.</summary>
    private static Type? FindType(Type from, string fullName)
    {
        for (var current = from; current is not null; current = current.BaseType)
        {
            if (current.Assembly.GetType(fullName) is { } found)
            {
                return found;
            }
        }

        return from.GetInterfaces().Select(type => type.Assembly.GetType(fullName)).FirstOrDefault(type => type is not null);
    }

    /// <summary>
    /// Calls a method of EF Core's metadata interfaces on an object. Most are default interface
    /// members, and which interface declares one changes between versions: whichever has it.
    /// </summary>
    private static object? Call(object target, string method, params object?[] arguments)
    {
        foreach (var type in MetadataInterfaces(target))
        {
            if (type.GetMethods().FirstOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length) is { } found)
            {
                return found.Invoke(target, arguments);
            }
        }

        return null;
    }

    private static object? Property(object target, string property) =>
        MetadataInterfaces(target).Select(type => type.GetProperty(property)).FirstOrDefault(found => found is not null)?.GetValue(target);

    /// <summary>The read-only interfaces first: the mutable ones redeclare members with other return types.</summary>
    private static IEnumerable<Type> MetadataInterfaces(object target) =>
        target.GetType().GetInterfaces()
            .Where(type => type.Namespace == Metadata.TrimEnd('.'))
            .OrderBy(type => type.Name.StartsWith("IReadOnly", StringComparison.Ordinal) ? 0 : type.Name.StartsWith("IMutable", StringComparison.Ordinal) || type.Name.StartsWith("IConvention", StringComparison.Ordinal) ? 2 : 1)
            .ThenBy(type => type.Name, StringComparer.Ordinal);
}
