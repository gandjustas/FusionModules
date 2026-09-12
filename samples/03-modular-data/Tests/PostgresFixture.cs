using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(ModularData.Tests.PostgresFixture))]

namespace ModularData.Tests;

/// <summary>
/// One PostgreSQL container for the whole test assembly, and a fresh database per topology.
/// </summary>
/// <remarks>
/// Every topology runs the same migrations against its own database, which is what makes the
/// interesting assertion possible: what a subset topology's schema looks like when the migrations
/// were generated against the union of every module.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

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
