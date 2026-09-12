using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Modulith;

namespace ModularData.Host;

/// <summary>
/// The application's one <see cref="DbContext"/>, whose model is whatever the loaded modules say
/// it is.
/// </summary>
/// <remarks>
/// This is the entire EF Core integration, and it is why there is no Modulith.EntityFrameworkCore
/// package: fifteen lines in your own context beats a base class you have to inherit and a
/// registration method you have to remember.
/// </remarks>
internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
    : DbContext(options)
{
    /// <summary>
    /// Identifies the module set this context was built for. Used as part of the model cache key.
    /// </summary>
    public string ModuleFingerprint { get; } =
        string.Join(';', ModuleBase.GetLoadedModules(configuration).Select(assembly => assembly.GetName().Name));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Activation order matters: a composing module's configuration has to be applied after
        // the configurations of the entities it joins. GetLoadedModules preserves the order of
        // HOSTINGSTARTUPASSEMBLIES, so "the composing module goes last" is a rule you can state.
        //
        // AppDomain.CurrentDomain.GetAssemblies() looks like it would do the same job and does
        // not: it reports assemblies that were referenced but never activated, and in a test
        // process it reports every module of every host that has run.
        foreach (var assembly in ModuleBase.GetLoadedModules(configuration))
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }
}

/// <summary>
/// Makes EF Core's model cache aware that the same context type has different models in
/// different topologies.
/// </summary>
/// <remarks>
/// EF caches a built model in an internal service provider shared by every context with the same
/// options, and the default cache key is the context type. Two hosts with different module sets
/// in one process — which is every integration-test assembly — would therefore share the first
/// one's model. The failure is silent: no exception, just the wrong tables.
/// </remarks>
internal sealed class ModuleAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is ApplicationDbContext app
        ? (typeof(ApplicationDbContext), app.ModuleFingerprint, designTime)
        : (object)(context.GetType(), designTime);
}
