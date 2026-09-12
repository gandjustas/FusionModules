using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ModularData.Tests;

/// <summary>
/// What each topology serves, against a real database.
/// </summary>
public class EndpointTests(PostgresFixture postgres)
{
    private async Task<ModularWebApplicationFactory> BootAsync(params string[] modules) =>
        new(await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken), modules);

    [Fact]
    public async Task EntityModulesAloneServeNothing()
    {
        // An entity module contributes tables, not routes. A topology of nothing but entity
        // modules is a database schema with a web server attached.
        using var factory = await BootAsync(KnownModules.Orders_Entities, KnownModules.Customers_Entities);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/customers", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/billing/overdue", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AFeatureModuleBringsItsOwnRoutesAndNobodyElses()
    {
        using var factory = await BootAsync(KnownModules.Customers_Entities, KnownModules.CustomersModule);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/customers", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/billing/overdue", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task TheFullTopologyJoinsAcrossModules()
    {
        using var factory = await BootAsync(KnownModules.All);
        var client = factory.CreateClient();

        var seeded = await client.PostAsync("/billing/seed", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

        var overdue = await client.GetFromJsonAsync<JsonElement>("/billing/overdue", TestContext.Current.CancellationToken);

        // The join is done by the database, across two schemas, because the composing module
        // declared a relationship between two other modules' entities.
        Assert.NotEmpty(overdue.EnumerateArray().ToArray());
        Assert.False(string.IsNullOrEmpty(overdue[0].GetProperty("customerName").GetString()));
    }

    [Fact]
    public async Task AFeatureModuleReadsTablesItDoesNotOwn()
    {
        using var factory = await BootAsync(KnownModules.All);
        var client = factory.CreateClient();

        await client.PostAsync("/billing/seed", content: null, TestContext.Current.CancellationToken);

        var customers = await client.GetFromJsonAsync<JsonElement>("/customers", TestContext.Current.CancellationToken);

        Assert.NotEmpty(customers.EnumerateArray().ToArray());
    }
}
