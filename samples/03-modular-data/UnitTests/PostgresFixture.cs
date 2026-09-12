using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Billing.UnitTests.PostgresFixture))]

namespace Billing.UnitTests;

/// <summary>
/// One PostgreSQL container for the whole test assembly, and a fresh database per test.
/// </summary>
/// <remarks>
/// The real provider, not an in-memory stand-in: schemas, decimal precision and query translation
/// are what the rules under test are expressed in, and a provider that quietly ignores
/// <c>ToTable("Orders", "Sales")</c> would make these tests agree with a production database they
/// do not describe.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{name}" """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ToString();
    }

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
