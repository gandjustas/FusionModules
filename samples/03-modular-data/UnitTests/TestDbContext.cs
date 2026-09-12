using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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
/// <para>
/// SQLite rather than the production provider, because a business rule is not a schema. What the
/// schema looks like per topology is asserted in the integration tests, against the real model;
/// what counts as an overdue order does not need a database server to answer.
/// </para>
/// </remarks>
internal sealed class TestDbContext : DbContext
{
    internal string AssemblySet { get; }

    private readonly SqliteConnection _connection;
    private readonly Assembly[] _configurationAssemblies;

    private TestDbContext(
        DbContextOptions<TestDbContext> options,
        SqliteConnection connection,
        Assembly[] configurationAssemblies)
        : base(options)
    {
        _connection = connection;
        _configurationAssemblies = configurationAssemblies;
        AssemblySet = string.Join(';', configurationAssemblies.Select(assembly => assembly.GetName().Name));
    }

    public static TestDbContext Create(params Assembly[] configurationAssemblies)
    {
        // Held open for the context's lifetime: an in-memory SQLite database exists only as long
        // as a connection to it does. EF does not own a connection it was handed, so neither does
        // it dispose one.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            // The same trap the host has to avoid, for the same reason. EF caches a built model
            // keyed by context type, so a second test composing a different set of assemblies
            // would silently get the first test's model.
            .ReplaceService<IModelCacheKeyFactory, AssemblySetModelCacheKeyFactory>()
            .Options;

        var context = new TestDbContext(options, connection, configurationAssemblies);
        context.Database.EnsureCreated();
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var assembly in _configurationAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Keys the model cache by the set of assemblies the model was composed from.
/// </summary>
internal sealed class AssemblySetModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is TestDbContext test
        ? (typeof(TestDbContext), test.AssemblySet, designTime)
        : (object)(context.GetType(), designTime);
}
