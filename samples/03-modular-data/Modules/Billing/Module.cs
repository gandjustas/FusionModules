using BillingModule;
using Customers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Orders;

[assembly: HostingStartup(typeof(Module))]

namespace BillingModule;

sealed class Module : Modulith.ModuleBase
{
    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints =>
        {
            var billing = endpoints.MapGroup("/billing");

            // The module takes a dependency on DbContext, not on the host's concrete context
            // type: it needs a place to put its tables, not knowledge of the application.
            billing.MapGet("/overdue", (DbContext db) => OverdueOrders.From(db, DateTimeOffset.UtcNow));

            billing.MapPost("/seed", async (DbContext db, CancellationToken cancellationToken) =>
            {
                if (await db.Set<Customer>().AnyAsync(cancellationToken))
                {
                    return Results.NoContent();
                }

                var customers = Enumerable.Range(1, 100)
                    .Select(i => new Customer { Id = i, Name = $"Customer {i}", Email = $"customer{i}@example.com" })
                    .ToArray();
                db.Set<Customer>().AddRange(customers);

                db.Set<Order>().AddRange(Enumerable.Range(0, 1000).Select(_ => new Order
                {
                    CustomerId = customers[Random.Shared.Next(customers.Length)].Id,
                    PlacedOn = DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(10_000)),
                    Amount = Random.Shared.Next(1, 1000),
                }));

                await db.SaveChangesAsync(cancellationToken);
                return Results.Created();
            });
        });
}
