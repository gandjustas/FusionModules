using BillingModule;
using Customers;
using Microsoft.EntityFrameworkCore;
using Orders;
using Xunit;

namespace Billing.UnitTests;

/// <summary>
/// The billing rule, tested without a host.
/// </summary>
/// <remarks>
/// Nothing here goes through HOSTINGSTARTUPASSEMBLIES. The module assemblies are referenced and
/// used like any other library, the test composes the model itself, and the rule is reached
/// through InternalsVisibleTo. Loading modules is the integration tests' job.
/// </remarks>
public class OverdueOrdersTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<TestDbContext> CreateContextAsync(CancellationToken cancellationToken) =>
        await TestDbContext.CreateAsync(
            await postgres.CreateDatabaseAsync(cancellationToken),
            cancellationToken,
            typeof(Order).Assembly,
            typeof(Customer).Assembly,
            // Last: its configuration adds a relationship to the two above.
            typeof(OverdueOrders).Assembly);

    private static async Task SeedAsync(TestDbContext db, CancellationToken cancellationToken, params Order[] orders)
    {
        db.Set<Customer>().Add(new Customer { Id = 1, Name = "Ada", Email = "ada@example.com" });
        db.Set<Order>().AddRange(orders);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Order Order(int id, DateTimeOffset placedOn, DateTimeOffset? paidOn = null) =>
        new() { Id = id, CustomerId = 1, PlacedOn = placedOn, PaidOn = paidOn, Amount = 100m };

    [Fact]
    public async Task AnUnpaidOrderPastTheGracePeriodIsOverdue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);
        await SeedAsync(db, cancellationToken, Order(1, Now.AddDays(-OverdueOrders.GraceDays - 1)));

        var overdue = await OverdueOrders.From(db, Now).ToListAsync(cancellationToken);

        Assert.Equal(1, Assert.Single(overdue).OrderId);
    }

    [Fact]
    public async Task AnOrderInsideTheGracePeriodIsNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);
        await SeedAsync(db, cancellationToken, Order(1, Now.AddDays(-OverdueOrders.GraceDays + 1)));

        Assert.Empty(await OverdueOrders.From(db, Now).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task APaidOrderIsNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);
        await SeedAsync(db, cancellationToken, Order(1, Now.AddDays(-30), paidOn: Now.AddDays(-29)));

        Assert.Empty(await OverdueOrders.From(db, Now).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task TheCustomerIsCarriedThrough()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);
        await SeedAsync(db, cancellationToken, Order(1, Now.AddDays(-10)));

        var overdue = Assert.Single(await OverdueOrders.From(db, Now).ToListAsync(cancellationToken));

        Assert.Equal("Ada", overdue.CustomerName);
        Assert.Equal("ada@example.com", overdue.CustomerEmail);
    }

    [Fact]
    public async Task OldestFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);
        await SeedAsync(db, cancellationToken,
            Order(1, Now.AddDays(-10)),
            Order(2, Now.AddDays(-30)),
            Order(3, Now.AddDays(-20)));

        var overdue = await OverdueOrders.From(db, Now).Select(order => order.OrderId).ToListAsync(cancellationToken);

        Assert.Equal([2, 3, 1], overdue);
    }

    [Fact]
    public async Task TheForeignKeyIsEnforced()
    {
        // The relationship comes from BillingModule's configuration, not from a navigation
        // property on Order — and because this test composed the model with that assembly last,
        // the database it created actually has the constraint.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var db = await CreateContextAsync(cancellationToken);

        // No customer was seeded, so this order points at nobody.
        db.Set<Order>().Add(Order(1, Now.AddDays(-10)));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken));
    }
}
