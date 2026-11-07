using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using FusionModules;
using FusionModules.EntityFrameworkCore;

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
/// <c>UseModules</c> names every module instead, and the names come from <c>KnownModules</c>,
/// which FusionModules generates from this project's references — a list written out by hand is a
/// copy of the project file that nothing keeps honest. This host is not a module itself; one that
/// is would add its own name, since a project does not reference itself.
/// </para>
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .UseModules(KnownModules.Names)
            .Options;

        return new ApplicationDbContext(options);
    }
}
