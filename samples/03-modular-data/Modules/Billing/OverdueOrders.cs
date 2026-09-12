using Customers;
using Microsoft.EntityFrameworkCore;
using Orders;

namespace BillingModule;

/// <summary>
/// What counts as an overdue order, and what the caller is told about it.
/// </summary>
/// <remarks>
/// Pulled out of the endpoint lambda so that it can be tested without a host: a business rule
/// should not need HOSTINGSTARTUPASSEMBLIES, an HTTP client or a running application to exercise.
/// The unit test project references this module's assembly directly and reaches this type through
/// InternalsVisibleTo.
/// </remarks>
internal static class OverdueOrders
{
    public const int GraceDays = 3;

    public static IQueryable<OverdueOrder> From(DbContext db, DateTime asOf) =>
        from order in db.Set<Order>()
        join customer in db.Set<Customer>() on order.CustomerId equals customer.Id
        where order.PaidOn == null && order.PlacedOn < asOf.AddDays(-GraceDays)
        orderby order.PlacedOn
        select new OverdueOrder(order.Id, order.PlacedOn, order.Amount, customer.Name, customer.Email);
}

internal readonly record struct OverdueOrder(
    int OrderId,
    DateTime PlacedOn,
    decimal Amount,
    string CustomerName,
    string CustomerEmail);
