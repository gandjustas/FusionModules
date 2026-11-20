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
        // The startup checks' switch turns off the model's check of table owners too (MOD0017).
        var checks = !string.Equals(configuration[TopologyConventions.RuntimeTopologyChecksKey], "false", StringComparison.OrdinalIgnoreCase);
        var modules = ModuleBase.GetLoadedModules(configuration);
        var extension = Extensions.GetValue(modules, modules => new ModulesOptionsExtension(modules, checks));
        if (extension.Checks != checks)
        {
            extension = new ModulesOptionsExtension(modules, checks);
        }

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

    /// <summary>
    /// Refuses a model composed from modules in which a table or an entity has two owning modules —
    /// both name the entity's table, or each maps an entity of its own onto one table (MOD0017). Off
    /// unless asked for: the host's build reports the same thing for the names written as constants.
    /// </summary>
    /// <param name="optionsBuilder">The context's options, which also call <c>UseModules</c>.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <example>
    /// <code>
    /// options.UseNpgsql(connectionString).UseModules(configuration).CheckModuleTableOwners();
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <see cref="ModuleModelBuilderExtensions.ApplyModuleConfigurations"/> notes, module by module,
    /// which module names which entity's table; when EF Core finalizes the model, the tables it ends up
    /// with are compared, so this sees what the build cannot: tables named by convention, names read
    /// from configuration, a default schema or mapping the context sets after the modules. The model
    /// then fails to build with an <see cref="InvalidOperationException"/> that names the table or the
    /// entity and the modules.
    /// </para>
    /// <para>
    /// Once per model, which is cached per set of modules: nothing per context or query. <c>dotnet ef</c>
    /// and tests get it from the same options. <c>FusionModules:RuntimeTopologyChecks=false</c> in the
    /// configuration handed to <c>UseModules</c> turns it off with the host's other startup checks.
    /// It needs EF Core's relational extensions, which every relational provider brings; without them
    /// there are no tables to compare.
    /// </para>
    /// </remarks>
    public static DbContextOptionsBuilder CheckModuleTableOwners(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(TableOwnersOptionsExtension.Instance);
        return optionsBuilder;
    }

    /// <inheritdoc cref="CheckModuleTableOwners(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> CheckModuleTableOwners<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)CheckModuleTableOwners((DbContextOptionsBuilder)optionsBuilder);

    /// <inheritdoc cref="UseModules(DbContextOptionsBuilder, string[])"/>
    public static DbContextOptionsBuilder<TContext> UseModules<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder, params string[] moduleAssemblyNames)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseModules((DbContextOptionsBuilder)optionsBuilder, moduleAssemblyNames);
}
