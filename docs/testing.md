# Testing a topology

`FusionModules.Testing` starts the host in a test with the modules the test names — the same
choice a deployment makes through `HOSTINGSTARTUPASSEMBLIES` — and reads back what composed.

```bash
dotnet add package FusionModules.Testing
```

It targets `net8.0` and `net10.0`, depends on `FusionModules` of the same version and on
`Microsoft.AspNetCore.Mvc.Testing` 8.0.0 or 10.0.0 and later respectively — the first release of
each, so the package raises nothing in a test project that references a later patch — and works
with any test framework: it has no opinion about TUnit, xUnit, NUnit or MSTest. Everything below is
in the `FusionModules.Testing` namespace.

## The factory

```csharp
using FusionModules.Testing;

using var factory = new ModularWebApplicationFactory<Program>("Orders.Entities", "OrdersModule");
var client = factory.CreateClient();
```

`ModularWebApplicationFactory<TEntryPoint>` is a `WebApplicationFactory<TEntryPoint>` that sets
`HOSTINGSTARTUPASSEMBLIES` to the names it is given; `Modules` holds them. No names is a topology
too: the host alone. A misspelt name fails startup when the host calls the generated
`ValidateModules()`, exactly as it would in a deployment.

The factory fails when the test process has `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` set. The web host
`WebApplicationFactory` wraps around the application reads its hosting startups from the
environment ahead of any setting, and runs those modules beside the ones the test named — the same
module twice maps its endpoints twice. A shell left with the variable set would run every topology
test against another topology; unset it for the test run.

`Program` has to be visible to the test project. .NET 10 makes the `Program` of top-level statements
public; on .NET 8, end `Program.cs` with `public partial class Program;`.

The class is not sealed. A topology that needs more than its module names overrides
`ConfigureWebHost` and calls the base:

```csharp
using FusionModules.Testing;
using Microsoft.AspNetCore.Hosting;          // IWebHostBuilder
using Microsoft.AspNetCore.TestHost;         // TestServer, ConfigureTestServices
using Microsoft.Extensions.DependencyInjection;

sealed class CheckoutTopology(TestServer pricing)
    : ModularWebApplicationFactory<Program>("CheckoutModule", "PricingClientModule")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Pricing:BaseAddress", "http://pricing.invalid");
        builder.ConfigureTestServices(services =>
            services.AddHttpClient("pricing").ConfigurePrimaryHttpMessageHandler(pricing.CreateHandler));
        base.ConfigureWebHost(builder);
    }
}
```

`WithWebHostBuilder` works as on any `WebApplicationFactory`, and keeps the modules.

## No database

```csharp
builder
    .UseConnectionStrings(
        ("Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"),
        ("Bus", "host=127.0.0.1;port=1"))
    .UseSetting("Database:Migrate", "false");
```

A topology test asks which modules, routes and model composed, not what they connect to. Building an
EF Core model needs a provider, not a connection. `UseConnectionStrings` sets each named connection
string, under `ConnectionStrings`, to the value given, verbatim. Give one that its client parses and
that nothing answers — port 1 on the loopback address: present, so a module that requires one finds
it; unreachable, so a test that does connect fails at once rather than finding a server somebody
left running.

Work the host does at startup — migrating, seeding — is the host's to switch off, with its own key:
`Database:Migrate` is the samples' convention, the switch a second replica turns off in a compose
file, not a FusionModules setting.

The value is yours to write because every client has its own format, and a string one client cannot
parse often fails late: an Npgsql-style string handed to EasyNetQ let the host start and failed on the
first use of the bus. Strings that parse and go nowhere:

| Client | Connection string |
|---|---|
| Npgsql (PostgreSQL) | `Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused` |
| SQL Server (`Microsoft.Data.SqlClient`) | `Server=127.0.0.1,1;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=true;Connect Timeout=1` |
| RabbitMQ through EasyNetQ | `host=127.0.0.1;port=1` |
| RabbitMQ through `RabbitMQ.Client` (a URI) | `amqp://127.0.0.1:1` |
| Redis (StackExchange.Redis) | `127.0.0.1:1,abortConnect=false,connectTimeout=100` |

`abortConnect=false` matters for Redis: without it a host that connects eagerly throws at startup.
The settings take precedence over the host's `appsettings.json`.

## No workers

```csharp
builder.WithoutApplicationHostedServices();
```

A module's `IHostedService` really runs inside `WebApplicationFactory`. A worker that loads data in
`StartAsync` or subscribes to a queue fails or hangs every topology test on a machine without that
infrastructure, for a reason that has nothing to do with composition. This removes every hosted
service except `GenericWebHostService`, which builds the pipeline: the modules' `Configure`, their
startup filters, the [startup checks](runtime.md) and the route table still happen. Work the host
does itself between `Build()` and `RunAsync()` is not a hosted service and still runs.

The extensions are opt-in, called from `ConfigureWebHost`:

```csharp
using FusionModules.Testing;
using Microsoft.AspNetCore.Hosting;

sealed class OfflineTopology(params string[] modules) : ModularWebApplicationFactory<Program>(modules)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseConnectionStrings(("Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"))
            .WithoutApplicationHostedServices()
            .UseSetting("Database:Migrate", "false");
        base.ConfigureWebHost(builder);
    }
}
```

The [startup report](runtime.md#the-startup-report) lists the hosted services left after this: a
worker a test removed does not run, and is not listed.

## Routes

```csharp
IReadOnlyList<ModuleRoute> routes = factory.Services.Routes();
// GET / (host)
// GET /openapi/{documentName}.json
// GET /orders/{id:int} (OrdersModule)
// * /orders/legacy (OrdersModule)
```

Every route the host matches requests against, read off the endpoint table rather than probed over
HTTP — a probe of an endpoint that queries the database cannot tell "not deployed" from "deployed and
broken". One `ModuleRoute(Module, Method, Pattern)` per HTTP method, `*` for a route that takes any,
ordered by pattern, then method, then module, so two inventories compare by value and `ToString()`
is a line of a snapshot file. Patterns start with a slash however they were written.

`Module` is who the [startup report](runtime.md#the-startup-report) counts the route for — one of:

- the loaded module that serves it: the module assembly that declares a minimal API's handler, a
  controller (wherever its action is declared), a Razor Page, a gRPC service or a SignalR hub;
- `host` (`ModuleRoute.Host`), for a route the host's own assembly (`IHostEnvironment.ApplicationName`)
  serves — `GET /` above. The same label whether or not the host is a module itself (its project
  carries `[assembly: HostingStartup(…)]`): the startup report's `endpoints` part counts those routes
  as `host` too, and a test that asks for the host's routes does not change when the host's module
  moves into a project of its own. A route of a loaded module is never `host`: only if a module's
  assembly were itself named `host` would the host's routes be named by the application's assembly
  instead, so the two stay apart;
- `null` for every other route: one whose handler the framework or a library declares —
  `MapOpenApi`'s `/openapi/{documentName}.json`, a health check, a static asset — or that has no
  handler method, a bare `RequestDelegate`. Nothing on such an endpoint says which module mapped it,
  and the library's assembly is no module.

Static assets are left out. `MapStaticAssets` (ASP.NET Core 9 and later) maps an endpoint per file,
and per compressed and fingerprinted copy, which in an application with a `wwwroot` or a Razor class
library is most of the table — about 534 of 542 routes in the first application converted — and
changes with every edit of a stylesheet; the `{**path:file}` catch-all it adds in Development goes
with them. `Routes(includeStaticAssets: true)` lists them, each with a `null` module. They are
recognised by their `StaticAssetDescriptor` metadata, matched by name, so the package's `net8.0`
build recognises them in a `net9.0` application too.

Reading the endpoints builds them, as the first request would, so a handler that cannot be built in
this topology fails the test here.

## A module in the test project

Do not put `[assembly: HostingStartup]` in the test assembly. While the hosting startups run, the host
`WebApplicationFactory` starts takes the entry assembly — the test runner — for the application, and
runs that assembly's hosting startups in every topology, named or not. A module the tests need goes in
a project of its own, referenced by the host or by the test project, like any other module.
