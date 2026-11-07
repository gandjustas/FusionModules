using System.Reflection;
using FusionModules.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Billing.UnitTests;

/// <summary>
/// A context built from exactly the modules the test names — no host, no environment variable.
/// </summary>
/// <remarks>
/// This is the counterpart to the integration tests. There, modules are strings handed to a
/// factory and the application assembles its own model. Here the test names the module assemblies
/// it references, and <c>UseModules</c> composes the same model the host would: each module after
/// the modules it references, so BillingModule's relationship lands after the entities it joins
/// whatever order the test lists them in, and one cached model per set of modules, so tests that
/// compose different sets do not share one.
/// <para>
/// SQLite rather than the production provider, because a business rule is not a schema. What the
/// schema looks like per topology is asserted in the integration tests, against the real model;
/// what counts as an overdue order does not need a database server to answer.
/// </para>
/// </remarks>
internal sealed class TestDbContext : DbContext
{
    private readonly SqliteConnection _connection;

    private TestDbContext(DbContextOptions<TestDbContext> options, SqliteConnection connection)
        : base(options)
    {
        _connection = connection;
    }

    public static TestDbContext Create(params Assembly[] modules)
    {
        // Held open for the context's lifetime: an in-memory SQLite database exists only as long
        // as a connection to it does. EF does not own a connection it was handed, so neither does
        // it dispose one.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .UseModules([.. modules.Select(module => module.GetName().Name!)])
            .Options;

        var context = new TestDbContext(options, connection);
        context.Database.EnsureCreated();
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyModuleConfigurations(this);
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }
}
