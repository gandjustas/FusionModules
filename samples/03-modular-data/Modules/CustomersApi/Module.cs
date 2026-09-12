using Customers;
using CustomersModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modulith;

[assembly: Microsoft.AspNetCore.Hosting.HostingStartup(typeof(Module))]

namespace CustomersModule;

sealed class Module : ModuleBase
{
    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapGet("/customers", (DbContext db, CancellationToken cancellationToken) =>
            db.Set<Customer>()
                .OrderBy(customer => customer.Id)
                .Select(customer => new { customer.Id, customer.Name, customer.Email })
                .Take(50)
                .ToListAsync(cancellationToken)));
}
