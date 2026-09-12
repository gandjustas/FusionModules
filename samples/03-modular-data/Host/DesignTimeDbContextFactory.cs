using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Modulith;

namespace ModularData.Host;

/// <summary>
/// Builds a context for <c>dotnet ef</c> against the union of every module.
/// </summary>
/// <remarks>
/// Migrations are generated for the full model and applied whole, never per topology. A
/// deployment that loads a subset of the modules simply has tables it does not use; a migration
/// per topology would give you a database whose shape depends on which replica reached it first.
/// <para>
/// <c>dotnet ef</c> never starts the host, so no module ever activates and the registry the model
/// is built from would be empty — which produces an empty migration and no error at all.
/// <see cref="ModuleBase.CreateModuleRegistry"/> fills it in.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Every module, unless HOSTINGSTARTUPASSEMBLIES says otherwise — which it should only do
    /// when you are deliberately inspecting one topology's model.
    /// </summary>
    private static string[] MigrationModules() =>
        Environment.GetEnvironmentVariable("HOSTINGSTARTUPASSEMBLIES") is { Length: > 0 } requested
            ? requested.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : KnownModules.All;
}
