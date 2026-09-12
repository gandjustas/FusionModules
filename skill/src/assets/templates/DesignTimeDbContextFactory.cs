// Builds a context for `dotnet ef` against the union of every module.
//
// dotnet ef DOES build the host, so HostingStartup runs and the model would follow
// HOSTINGSTARTUPASSEMBLIES as it stands in the shell that ran the command: empty when it is unset,
// one topology's tables when it is not, and no error either way. Naming every module here instead
// makes the migration the same on every machine and in CI.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Modulith;

namespace Host;

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(AllModules))
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>()
            .Options;

        return new ApplicationDbContext(options, configuration);
    }

    // Every module, as HOSTINGSTARTUPASSEMBLIES spells them. Deliberately not read from the
    // environment: migrations are generated against the union and applied whole, and a variable
    // left over from debugging one topology would otherwise produce a migration for that topology
    // without saying so.
    private static readonly string[] AllModules = ["Orders.Entities", "Customers.Entities", "OrdersModule"];
}
