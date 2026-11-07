// Topology tests over FusionModules.Testing:
//
//   dotnet add package FusionModules.Testing
//
// ModularWebApplicationFactory<Program> boots the host with a chosen set of modules — the same
// choice a deployment makes through HOSTINGSTARTUPASSEMBLIES, so a test topology and a production
// topology are the same thing. It works with any test framework, and brings
// Microsoft.AspNetCore.Mvc.Testing with it.
//
// The subclass below is only needed when a topology needs more than its module names. Without one:
//
//   using var factory = new ModularWebApplicationFactory<Program>("OrdersModule");

using FusionModules.Testing;
using Microsoft.AspNetCore.Hosting;

namespace Tests;

// A host that comes up without opening a single connection, and without its modules' workers.
// Both are opt-in: a module with no worker that touches infrastructure needs neither.
//
// A module's IHostedService really runs inside WebApplicationFactory, so a worker that loads
// translations from a database in StartAsync makes every topology test fail on a machine that has
// no database — and the failure says nothing about composition, which is the only thing these
// tests ask about. WithoutApplicationHostedServices keeps GenericWebHostService: it is what builds
// the pipeline, and therefore the route table the test came for.
internal sealed class OfflineTopology(params string[] modules) : ModularWebApplicationFactory<Program>(modules)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            // Each named connection string set as given: in its own client's format, pointing at an
            // address nothing listens on. EasyNetQ, say, takes ("RabbitMQ", "host=127.0.0.1;port=1"),
            // and Redis "127.0.0.1:1,abortConnect=false".
            .UseConnectionStrings(("Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"))
            .WithoutApplicationHostedServices()

            // The gate the host runs between Build() and RunAsync(), and the same switch a second
            // replica uses in the compose file: a convention of the host, not a FusionModules setting.
            .UseSetting("Database:Migrate", "false");

        base.ConfigureWebHost(builder);
    }
}

// The route table, with the module that serves each route:
//
//   using var factory = new OfflineTopology("Orders.Entities", "OrdersModule");
//   var routes = factory.Services.Routes();   // GET /orders/{id} (OrdersModule), ...
//
// The module is a loaded module or the host; null for a route the framework or a library serves
// (MapOpenApi's document, a health check). Static assets are left out unless asked for:
// Routes(includeStaticAssets: true).
