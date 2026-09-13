// The whole EF Core integration. There is no Modulith EF package because this is what it would
// contain, and a base class to inherit plus a method to remember is worse than fifteen lines you
// can read.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Modulith;

namespace Host;

internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
    : DbContext(options)
{
    /// <summary>Identifies the module set this context was built for. Part of the model cache key.</summary>
    public string ModuleFingerprint { get; } =
        string.Join(';', ModuleBase.GetLoadedModules(configuration).Select(assembly => assembly.GetName().Name));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Activation order matters: a composing module's configuration has to be applied after the
        // configurations of the entities it joins, so it goes last in HOSTINGSTARTUPASSEMBLIES.
        //
        // Not AppDomain.CurrentDomain.GetAssemblies(): filtered by the attribute it is correct
        // while one host owns the process, and wrong as soon as a second one shares it — in an
        // integration-test assembly every topology's modules are loaded, so every topology
        // composes the union. Nothing throws; the tables are wrong.
        foreach (var assembly in ModuleBase.GetLoadedModules(configuration))
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }
}

/// <summary>
/// Makes EF Core's model cache aware that one context type has different models in different
/// topologies.
/// </summary>
/// <remarks>
/// EF caches a built model in an internal service provider shared by every context with the same
/// options, keyed by context type. Two hosts with different module sets in one process — every
/// integration test assembly — otherwise share the first one's model. Nothing throws; the tables
/// are simply wrong.
/// </remarks>
internal sealed class ModuleAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is ApplicationDbContext application
        ? (typeof(ApplicationDbContext), application.ModuleFingerprint, designTime)
        : (object)(context.GetType(), designTime);
}
