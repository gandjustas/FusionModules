using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tool.Probe;

/// <summary>
/// DbContext types by name: the probe has no compile-time EF Core reference, because it has to load
/// into applications on EF 8, 9 and 10 alike, and into ones without EF at all.
/// </summary>
internal static class DbContexts
{
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";

    public static void Describe(List<ServiceDescriptor> snapshot, IServiceProvider services, ProbeOutput result)
    {
        var contexts = new Dictionary<Type, ProbeDbContext>();
        foreach (var descriptor in snapshot)
        {
            if (descriptor.IsKeyedService)
            {
                continue;
            }

            var type = descriptor.ServiceType;
            if (!type.IsGenericTypeDefinition && IsDbContext(type))
            {
                if (!contexts.TryGetValue(type, out var context))
                {
                    contexts[type] = context = new ProbeDbContext { Type = TypeNames.Of(type), Assembly = type.Assembly.GetName().Name };
                }

                // AddDbContextPool registers the pool before the context itself.
                context.Lifetime ??= descriptor.Lifetime.ToString();
            }

            // AddDbContextPool registers the pool by the context's type argument.
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName is "Microsoft.EntityFrameworkCore.Internal.IDbContextPool`1"
                && type.GetGenericArguments()[0] is var pooled && IsDbContext(pooled))
            {
                if (!contexts.TryGetValue(pooled, out var context))
                {
                    contexts[pooled] = context = new ProbeDbContext { Type = TypeNames.Of(pooled), Assembly = pooled.Assembly.GetName().Name };
                }

                context.Pooled = true;
            }
        }

        // A pooled factory alone (AddPooledDbContextFactory) registers no context: the factory hands
        // out leases, and each lease is the caller's to dispose.
        foreach (var context in contexts.Values.Where(c => c.Lifetime is null && c.Pooled))
        {
            context.Lifetime = "Pooled";
        }

        if (contexts.Count == 0)
        {
            return;
        }

        result.DbContexts = [];
        foreach (var (type, context) in contexts)
        {
            try
            {
                ReadOptions(type, services, context);
                if (context.Provider is null)
                {
                    // Configured in OnConfiguring rather than through DbContextOptions<T>: what
                    // `dotnet ef` does — create the context and ask it. Creating one connects to nothing.
                    ReadFromInstance(type, services, context);
                }
            }
            catch (Exception exception)
            {
                var error = ProbeError.From("dbcontext", exception);
                context.Error = $"{error.Type}: {error.Message}";
            }

            DbContextModels.Read(type, services, context);
            result.DbContexts.Add(context);
        }
    }

    private static bool IsDbContext(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == DbContextType)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadOptions(Type contextType, IServiceProvider services, ProbeDbContext context)
    {
        var efAssembly = BaseOf(contextType).Assembly;
        var optionsType = efAssembly.GetType("Microsoft.EntityFrameworkCore.DbContextOptions`1")?.MakeGenericType(contextType);
        if (optionsType is null)
        {
            return;
        }

        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetService(optionsType);
        if (options?.GetType().GetProperty("Extensions")?.GetValue(options) is not IEnumerable extensions)
        {
            return;
        }

        foreach (var extension in extensions)
        {
            var type = extension.GetType();
            if (HasBase(type, "Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension"))
            {
                context.Provider = type.Assembly.GetName().Name;
                context.ConnectionString = Secrets.MaskConnectionString(type.GetProperty("ConnectionString")?.GetValue(extension) as string);
                context.MigrationsAssembly = type.GetProperty("MigrationsAssembly")?.GetValue(extension) as string;
                var table = type.GetProperty("MigrationsHistoryTableName")?.GetValue(extension) as string;
                var schema = type.GetProperty("MigrationsHistoryTableSchema")?.GetValue(extension) as string;
                if (table is not null || schema is not null)
                {
                    context.MigrationsHistoryTable = schema is null ? table : $"{schema}.{table ?? "__EFMigrationsHistory"}";
                }
            }
            else if (context.Provider is null
                     && type.Name.EndsWith("OptionsExtension", StringComparison.Ordinal)
                     && type.Assembly != efAssembly
                     && !type.Assembly.GetName().Name!.StartsWith("Microsoft.EntityFrameworkCore.Proxies", StringComparison.Ordinal))
            {
                // A non-relational provider: Cosmos, InMemory.
                context.Provider = type.Assembly.GetName().Name;
            }
        }
    }

    private static void ReadFromInstance(Type contextType, IServiceProvider services, ProbeDbContext context)
    {
        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService(contextType) is not { } instance)
        {
            return;
        }

        try
        {
            var database = contextType.GetProperty("Database")?.GetValue(instance);
            context.Provider = database?.GetType().GetProperty("ProviderName")?.GetValue(database) as string;
            var relational = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Relational")
                ?.GetType("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions");
            if (database is not null && relational?.GetMethod("GetConnectionString", [database.GetType()]) is { } getConnectionString)
            {
                context.ConnectionString = Secrets.MaskConnectionString(getConnectionString.Invoke(null, [database]) as string);
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    private static Type BaseOf(Type contextType)
    {
        var current = contextType;
        while (current.BaseType is not null && current.FullName != DbContextType)
        {
            current = current.BaseType;
        }

        return current;
    }

    private static bool HasBase(Type type, string fullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }
}
