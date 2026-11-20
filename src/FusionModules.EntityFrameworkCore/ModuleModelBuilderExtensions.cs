using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// Composes a context's model from its modules' entity configurations.
/// </summary>
public static class ModuleModelBuilderExtensions
{
    /// <summary>
    /// Applies every <see cref="IEntityTypeConfiguration{TEntity}"/> of the modules the context's
    /// options name (<see cref="ModuleDbContextOptionsExtensions.UseModules(DbContextOptionsBuilder, Microsoft.Extensions.Configuration.IConfiguration)"/>),
    /// in the order <see cref="ModuleBase.GetLoadedModules"/> returns them: the host's own module
    /// first, then each module after the modules it references.
    /// </summary>
    /// <param name="modelBuilder">The model builder of <c>OnModelCreating</c>.</param>
    /// <param name="context">The context whose model is being built: <c>this</c>.</param>
    /// <returns>The same model builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The context's options do not name its modules; or two of the modules own one table or one
    /// entity (MOD0017): both name its table, or each maps an entity of its own onto it.
    /// </exception>
    /// <example>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    ///     modelBuilder.ApplyModuleConfigurations(this);
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// A module without configurations is skipped rather than scanned again by EF Core. Configurations
    /// are found among the types that load, so a module whose optional dependency is not deployed
    /// still has its configurations applied.
    /// </para>
    /// <para>
    /// A table and an entity have one owning module, the one that names the table; where two modules
    /// own one, the model is refused (MOD0017, whose build half sees only constant names; this sees
    /// tables named by convention and from configuration too). The check runs once per model, which
    /// is cached per set of modules, and is off where <c>FusionModules:RuntimeTopologyChecks</c> is
    /// <c>false</c> in the configuration handed to <c>UseModules</c>.
    /// </para>
    /// <para>
    /// The model's composition is written to the log once per set of modules (category
    /// <c>FusionModules.EntityFrameworkCore</c>): the modules in the order applied, those without
    /// configurations, and the entities several modules configure.
    /// </para>
    /// </remarks>
    public static ModelBuilder ApplyModuleConfigurations(this ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        var modules = context.GetService<IDbContextOptions>().FindExtension<ModulesOptionsExtension>()
            ?? throw new InvalidOperationException(
                $"'{context.GetType().Name}' composes its model from the loaded modules, but its options do not say which " +
                "modules are loaded. Call UseModules on them: options.UseModules(configuration) in a host, " +
                "options.UseModules(KnownModules.Names) for dotnet ef and tests.");

        var loaded = modules.Modules.Select(module => (Assembly: module, Module: ModuleEntities.Of(module))).ToList();

        ModuleTables.Apply(
            modelBuilder,
            [.. loaded.Select(pair => new ModuleTables.Contribution(
                pair.Module.Name,
                pair.Assembly,
                pair.Module.Entities,
                pair.Module.Entities.Count > 0 ? model => model.ApplyConfigurationsFromAssembly(pair.Assembly) : static _ => { }))],
            context.GetType().Name,
            modules.Checks);

        Report(context, [.. loaded.Select(pair => pair.Module)]);
        return modelBuilder;
    }

    /// <summary>
    /// One line per model, like the host's <c>Topology:</c> line: which modules the model came from and
    /// in what order, which had nothing to add, and which entities more than one module configures —
    /// in the order their configurations were applied. Models are cached per set of modules, so this
    /// is written once per model built — per set and per design-time flag — not per context.
    /// </summary>
    private static void Report(DbContext context, IReadOnlyList<ModuleEntities.Module> modules)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger("FusionModules.EntityFrameworkCore");
        if (logger is null || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var configuring = modules.Where(module => module.Entities.Count > 0).ToList();
        var applied = string.Join(", ", configuring.Select(module =>
            $"{module.Name} ({string.Join(", ", module.Entities.Select(ModuleEntities.Readable).Order(StringComparer.Ordinal))})"));
        var without = string.Join(", ", modules.Where(module => module.Entities.Count == 0).Select(module => module.Name));
        var shared = string.Join(", ", configuring
            .SelectMany(module => module.Entities.Select(entity => (Entity: entity, Module: module.Name)))
            .GroupBy(pair => pair.Entity)
            .Where(group => group.Count() > 1)
            .Select(group => $"{ModuleEntities.Readable(group.Key)} ({string.Join(", then ", group.Select(pair => pair.Module))})"));

        Write(logger, context.GetType().Name, applied, without, shared);
    }

    /// <summary>The line, written the way the host's <c>Topology:</c> line is (<see cref="PartsLogLine"/>).</summary>
    private static void Write(ILogger logger, string context, string applied, string without, string shared) =>
        PartsLogLine.Write(
            logger,
            ("Model of {Context}: ", $"Model of {context}: "),
            [new("Context", context)],
            [
                ("configurations from", "Modules", applied),
                ("without configurations", "Without", without),
                ("configured by several modules", "Shared", shared),
            ],
            whenEmpty: "no modules");
}
