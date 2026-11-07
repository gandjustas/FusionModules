using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// Tells a context which modules its model is composed from.
/// </summary>
public static class ModuleDbContextOptionsExtensions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<System.Reflection.Assembly>, ModulesOptionsExtension> Extensions = new();

    /// <summary>
    /// Composes the context's model from the modules activated in this host, and caches one model
    /// per set of modules.
    /// </summary>
    /// <param name="optionsBuilder">The context's options.</param>
    /// <param name="configuration">The host's configuration, where the modules register as they activate.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The modules are those of <see cref="ModuleBase.GetLoadedModules"/>, in its order: the host's own
    /// assembly first when it is a module, then each module after the modules it references, so a
    /// module that adds to another's entity configuration is applied after it. The context applies
    /// their configurations with
    /// <see cref="ModuleModelBuilderExtensions.ApplyModuleConfigurations"/> in <c>OnModelCreating</c>.
    /// </para>
    /// <para>
    /// The model cache key includes the module set, so hosts with different modules in one process —
    /// an integration-test assembly — each get their own model rather than the first one's. For that
    /// it replaces EF Core's <c>IModelCacheKeyFactory</c>: a context that replaces it too puts
    /// <see cref="ModuleDbContextExtensions.GetModuleSetKey"/> into its own key.
    /// </para>
    /// </remarks>
    public static DbContextOptionsBuilder UseModules(this DbContextOptionsBuilder optionsBuilder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);

        // One extension per module set: GetLoadedModules hands back the same list for the same registry,
        // and the options are built again for every scope.
        var extension = Extensions.GetValue(ModuleBase.GetLoadedModules(configuration), static modules => new ModulesOptionsExtension(modules));
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder.ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>();
    }

    /// <inheritdoc cref="UseModules(DbContextOptionsBuilder, IConfiguration)"/>
    public static DbContextOptionsBuilder<TContext> UseModules<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder, IConfiguration configuration)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseModules((DbContextOptionsBuilder)optionsBuilder, configuration);

    /// <summary>
    /// Composes the context's model from the named modules, outside a host: for <c>dotnet ef</c> and
    /// for tests. Usually <c>KnownModules.Names</c>, every module the project references.
    /// </summary>
    /// <param name="optionsBuilder">The context's options.</param>
    /// <param name="moduleAssemblyNames">The modules' assembly names.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <remarks>
    /// The same as <see cref="UseModules(DbContextOptionsBuilder, IConfiguration)"/> over the registry
    /// that <see cref="ModuleBase.CreateModuleRegistry"/> builds — so the order is the same as in a
    /// host: each module after the modules it references. Only the named assemblies count:
    /// <c>KnownModules.Names</c> lists the modules a project references, so a host that is itself a
    /// module is not among them, and its configurations are left out unless its name is added.
    /// </remarks>
    public static DbContextOptionsBuilder UseModules(this DbContextOptionsBuilder optionsBuilder, params string[] moduleAssemblyNames)
    {
        ArgumentNullException.ThrowIfNull(moduleAssemblyNames);

        var registry = new ConfigurationBuilder()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(moduleAssemblyNames))
            .Build();
        return UseModules(optionsBuilder, registry);
    }

    /// <inheritdoc cref="UseModules(DbContextOptionsBuilder, string[])"/>
    public static DbContextOptionsBuilder<TContext> UseModules<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder, params string[] moduleAssemblyNames)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseModules((DbContextOptionsBuilder)optionsBuilder, moduleAssemblyNames);
}
