// The application's one context, composed from the modules the host loaded. Needs the
// FusionModules.EntityFrameworkCore package.
//
// Register it with the modules named in its options — UseModules also gives EF Core a model cache
// key that includes the module set, so hosts with different modules in one process (any
// integration-test assembly) do not share the first one's model:
//
//   builder.Services.AddDbContext<ApplicationDbContext>(options => options
//       .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
//       .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
//       .UseModules(builder.Configuration));
//   builder.Services.AddTransient<DbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
//
// UseModules replaces EF Core's IModelCacheKeyFactory. A context that replaces it too puts
// context.GetModuleSetKey() into its own key.

using Microsoft.EntityFrameworkCore;
using FusionModules.EntityFrameworkCore;

namespace Host;

internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Every IEntityTypeConfiguration of the modules UseModules named, in GetLoadedModules
        // order: the host's own module first when the host is one, then each module after the
        // modules it references — a composing module's relationships are applied after the entity
        // modules it joins, wherever HOSTINGSTARTUPASSEMBLIES lists it.
        modelBuilder.ApplyModuleConfigurations(this);
    }
}
