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
/// <c>dotnet ef</c> does build the host, so <c>HostingStartup</c> runs and the model would follow
/// <c>HOSTINGSTARTUPASSEMBLIES</c> as it stands in the shell that ran the command — an empty
/// migration and no error at all when it is unset, one topology's tables when it is not.
/// <see cref="ModuleBase.CreateModuleRegistry"/> names every module here instead, so the migration
/// is the same on every machine and in CI.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Every module, as HOSTINGSTARTUPASSEMBLIES spells them. Deliberately not read from the
    /// environment: migrations are generated against the union and applied whole, and a variable
    /// left over from debugging one topology would otherwise produce a migration for that topology
    /// without saying so.
    /// </summary>
    private static readonly string[] AllModules =
        ["Orders.Entities", "Customers.Entities", "CustomersModule", "BillingModule"];
}
