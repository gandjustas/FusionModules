using BillingModule;
using Customers;
using Orders;
using Xunit;

namespace Billing.UnitTests;

/// <summary>
/// The billing rule, tested without a host.
/// </summary>
/// <remarks>
/// Nothing here goes through HOSTINGSTARTUPASSEMBLIES. The module assemblies are referenced and
/// used like any other library, the test composes the model itself, and the rule is reached
/// through InternalsVisibleTo — MOD0001 means everything worth testing in a module is internal.
/// Loading modules is the integration tests' job.
/// </remarks>
public class OverdueOrdersTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TestDbContext CreateContext() => TestDbContext.Create(
        typeof(Order).Assembly,
        typeof(Customer).Assembly,
        // Last: its configuration adds a relationship to the two above.
        typeof(OverdueOrders).Assembly);

    private static void Seed(TestDbContext db, params Order[] orders)
    {
        db.Set<Customer>().Add(new Customer { Id = 1, Name = "Ada", Email = "ada@example.com" });
        db.Set<Order>().AddRange(orders);
        db.SaveChanges();
    }

    private static Order Order(int id, DateTime placedOn, DateTime? paidOn = null) =>
        new() { Id = id, CustomerId = 1, PlacedOn = placedOn, PaidOn = paidOn, Amount = 100m };

    [Fact]
    public void AnUnpaidOrderPastTheGracePeriodIsOverdue()
    {
        using var db = CreateContext();
        Seed(db, Order(1, Now.AddDays(-OverdueOrders.GraceDays - 1)));

        var overdue = OverdueOrders.From(db, Now).ToList();

        Assert.Equal(1, Assert.Single(overdue).OrderId);
    }

    [Fact]
    public void AnOrderInsideTheGracePeriodIsNot()
    {
        using var db = CreateContext();
        Seed(db, Order(1, Now.AddDays(-OverdueOrders.GraceDays + 1)));

        Assert.Empty(OverdueOrders.From(db, Now));
    }

    [Fact]
    public void APaidOrderIsNot()
    {
        using var db = CreateContext();
        Seed(db, Order(1, Now.AddDays(-30), paidOn: Now.AddDays(-29)));

        Assert.Empty(OverdueOrders.From(db, Now));
    }

    [Fact]
    public void TheCustomerIsCarriedThrough()
    {
        using var db = CreateContext();
        Seed(db, Order(1, Now.AddDays(-10)));

        var overdue = Assert.Single(OverdueOrders.From(db, Now).ToList());

        Assert.Equal("Ada", overdue.CustomerName);
        Assert.Equal("ada@example.com", overdue.CustomerEmail);
    }

    [Fact]
    public void OldestFirst()
    {
        using var db = CreateContext();
        Seed(db, Order(1, Now.AddDays(-10)), Order(2, Now.AddDays(-30)), Order(3, Now.AddDays(-20)));

        Assert.Equal([2, 3, 1], OverdueOrders.From(db, Now).Select(order => order.OrderId).ToList());
    }

    [Fact]
    public void WithoutTheComposingAssembly_TheJoinHasNothingToJoinTo()
    {
        // Leaving BillingModule's own assembly out of the composition is the unit-test equivalent
        // of leaving the module out of a topology: the relationship it declares is simply not
        // there, and the test says so instead of quietly passing.
        using var db = TestDbContext.Create(typeof(Order).Assembly, typeof(Customer).Assembly);

        Assert.Empty(db.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()));
    }
}
