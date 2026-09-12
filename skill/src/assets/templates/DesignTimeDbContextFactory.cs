// Builds a context for `dotnet ef` against the union of every module.
//
// dotnet ef never starts the host, so no module activates and the registry the model is built
// from is empty — which produces an empty migration and no error at all. CreateModuleRegistry
// fills it in. If your generated migration's Up() is empty, this is why.

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
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(MigrationModules()))
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>()
            .Options;

        return new ApplicationDbContext(options, configuration);
    }

    // Every module, unless HOSTINGSTARTUPASSEMBLIES says otherwise — which it should only do when
    // you are deliberately inspecting one topology's model. Migrations are always generated
    // against the union and applied whole; a topology loading a subset simply has tables it does
    // not use.
    private static string[] MigrationModules() =>
        Environment.GetEnvironmentVariable("HOSTINGSTARTUPASSEMBLIES") is { Length: > 0 } requested
            ? requested.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : KnownModules.All;
}
