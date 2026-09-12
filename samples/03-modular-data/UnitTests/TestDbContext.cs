using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Billing.UnitTests;

/// <summary>
/// A context built from exactly the assemblies the test names — no host, no module activation,
/// no environment variable.
/// </summary>
/// <remarks>
/// This is the counterpart to the integration tests. There, modules are strings handed to a
/// factory and the application assembles its own model. Here the test composes the model: it says
/// which assemblies it wants and calls <c>ApplyConfigurationsFromAssembly</c> for each — the same
/// mechanism the host uses, driven by a project reference rather than by a registry.
/// <para>
/// Composition order is the test's responsibility too: the configuration in BillingModule adds a
/// relationship to entities configured elsewhere, so its assembly goes last.
/// </para>
/// </remarks>
internal sealed class TestDbContext(DbContextOptions<TestDbContext> options, Assembly[] configurationAssemblies)
    : DbContext(options)
{
    public static async Task<TestDbContext> CreateAsync(
        string connectionString,
        CancellationToken cancellationToken,
        params Assembly[] configurationAssemblies)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            // A schema per test, so tests sharing the container do not share tables.
            .UseNpgsql(connectionString)
            .Options;

        var context = new TestDbContext(options, configurationAssemblies);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var assembly in configurationAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }
}
