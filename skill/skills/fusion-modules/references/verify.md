# Verification

Run this after every phase, not at the end. A migration that is verified only once is a migration
whose failures all arrive together.

## 1. Capture the baseline first

Before touching anything, record what each service serves. This is the single most useful
artefact of the whole migration: a mechanical conversion that preserves the route set is very
likely correct, and one that does not hands you a concrete, reviewable diff instead of a feeling.

```csharp
services.GetRequiredService<EndpointDataSource>()
    .Endpoints
    .OfType<RouteEndpoint>()
    .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
    .Order(StringComparer.Ordinal)
```

Read off the endpoint table, not probed over HTTP: an HTTP probe cannot tell "not deployed" from
"deployed and broken", and it drags a database into a question about composition. Swagger JSON
works too if the services already produce it.

Store one file per service, then one per topology, and diff them. `dotnet modules inventory -o
after.json --compare before.json` does it for a whole solution, host by host, and
`dotnet modules topology inventory.json <services…>` previews what one host composing them would
fail on before it exists. After the conversion the topology
side can come from `Routes()` (section 4): `Routes().Select(route => $"{route.Method} {route.Pattern}").Distinct().Order(StringComparer.Ordinal)`
writes the template's lines — one method per line, `*` for any.

Reading a comparison:

- **Exit code 2 means "different", not "failed".** After a step that was meant to change something
  — a route prefix, a named policy — the differences are the review. Each one gets an explanation
  or a fix; "about a hundred, all explained, no endpoint lost" is a pass.
- **`winner` lines are the ones to read first.** `- winner IProfileService -> Shop.ProfileService`
  after moving registrations into a module means another registration of that service now comes
  last: the build is green, login works, and the token has different claims.
- **A count of a framework library's registrations off by one** (`Microsoft.Extensions.Options:
  111 → 112`) is noise.
- **Same environment on both sides.** An inventory taken in Development against one taken in
  Production differs by everything Development maps. The same goes for configuration: a solution's
  inventory takes connection strings from the AppHost or compose files, a host project probed alone
  takes its launch profile. Registrations made on a condition (`if` an address is configured)
  vanish, a `DbContext` comes and goes, or the host fails on `Connection string '…' is required`.
  Pass the values (`--env ConnectionStrings__orders=…`) or point the deployment at the new host and
  inventory the solution.
- **Read each host's status before its differences.** A host that failed to build or start gives a
  wall of them — hundreds, with the error at the bottom — and "0 hosts compared" means one side
  has no probed hosts at all. A `DbContext` entry carrying an `error` is not a difference in the
  model.
- **Same infrastructure on both sides too.** With a database down, the exception a host reports
  can differ from run to run (a `SqlException` once, a retry failure the next time). Another
  exception type at the same place is noise.
- **Startup work that became a hosted service changes the picture legitimately**: `built →
  started`, `+ hosted service …`, and the startup error gone. The probe does not start hosted
  services, so an error gone from there is not an error fixed — run the service against a database
  to see whether the work still happens.
- **Every `- registration` is real until explained.** A consumer, a hub, an outbox that is no
  longer registered leaves a process that builds, starts and answers 200.
- **For `--merged`, take "before" after the hand preparation**, or most of what it reports is your
  own edits. A deliberate keyed replacement reads as a `-`/`+` pair, and so does a changed
  authorization policy on an endpoint.

Freeze the baseline into the test project as constants — routes, own registrations, hosted
services, event subscriptions, tables — generated once from the "before" inventory and never
edited. What the migration edits is one small table: for each topology, the entry point, the module
names and a route prefix. While a service is its own host the comparison is exact; once it is a
module in someone else's host it is "nothing lost".

Normalise the leading slash. An attribute route is stored without one and a minimal-API route with
one, so `/orders` and `orders` are the same URL written two ways — and converting a controller to a
minimal API would otherwise diff every route it touched while changing none of them. The template
does this, and so does `Routes()` from FusionModules.Testing (section 4); if you write your own, do
it too.

### When every service declares `Program` in the global namespace

Which is every service converted from top-level statements. One test project referencing fifteen
of them sees fifteen `Program` types, and usually a few genuinely duplicated type names between
services that were copied from one another. It does not compile, and the error names a type rather
than the shape of the problem.

Give each reference its own alias and reach the entry point through it:

```xml
<ProjectReference Include="..\..\orders\Orders.csproj" Aliases="svc_orders" />
<NoWarn>$(NoWarn);CS0436</NoWarn>
```

```csharp
extern alias svc_orders;

internal sealed class OrdersFactory : WebApplicationFactory<svc_orders::Program>;
```

An ambiguous name is an error only where it is used, and through an alias it never is; CS0436 is
the warning about the ambiguity you have just arranged not to hit. This belongs to the test project
that references many services at once, which is a temporary thing — when the old projects are
deleted, the aliases go with them and the topology factory goes back to plain `Program`.

If that is more machinery than one baseline is worth, run one process per service and capture each
inventory on its own. The file per service is the artefact; where it was produced does not matter.

## 2. What the inventory cannot see

It is the best signal you have and it is not a complete one. Three classes of failure leave the
inventory byte-identical and the process broken.

**Anything built by reflection at resolve time.** The client proxy of a SignalR `Hub<TClient>`, a
`JsonSerializerContext` in another assembly, `ActivatorUtilities` over a type named in
configuration, a gRPC service base. The endpoint is in the table; the first request into it throws.

**Anything negotiated on the wire.** Protocol, TLS, ALPN, authentication scheme. The table says a
gRPC method is mapped — whether a gRPC client can reach it is a property of the Kestrel endpoint
the call arrives on, and that is in no route. See [the host](host.md).

**Anything that happens after the container is built.** A connection string that resolves to a
password nothing else has, a schema gate that never completes, a hosted service that does not
return from `StartAsync`, a singleton whose constructor throws. A factory that builds a host and
reads its endpoints touches none of it.

The answer is not a better inventory. It is that section 4 is this section's peer rather than its
sequel: boot every topology for real, and put one real request through each module.

Each of these has been green on a broken result, on a real solution:

- the tool's exit code 0, with compile errors in the next build;
- the host building, with the solution's test projects not — build the whole solution;
- `started` under the probe, and a crash in `dotnet run` on a file read from the content root;
- the host starting, and every request answering 500;
- `/health` answering Healthy, with most of the checks gone;
- checkout answering 200, and no order created — the consumer was not registered;
- 401 on a protected route, which proves only that the middleware is there;
- unit tests of a handler, while the subscription that calls it is gone.

What did catch them: the comparison before and after each move; `--merged`; a build of everything;
and one fixed script of requests run against the original and against the result with real
infrastructure, its output diffed. Put in that script an unauthenticated request to every family of
endpoints — a 500 from a backend where 401 was expected means the request got past authorization —
and a request with a real token, a message through the bus, and a gRPC call between modules. Those
three are the ones that get skipped, and the ones a merge breaks. Check state, not status: the
order row, the migration history, the number of consumers on the queue.

Take a baseline of what was already broken. An endpoint that answered 500 before the migration, or
a solution that never restored as a whole, is not yours to explain later.

## 3. Build

```bash
dotnet build -warnaserror
```

Zero MOD diagnostics. If MOD0004 fires alongside compiler errors, fix the compiler errors first —
Roslyn cannot work out which references are used in a broken compilation, and the analyzer
suppresses itself in that case, so anything you do see is real.

## 4. Boot every topology

```
dotnet add package FusionModules.Testing
```

```csharp
using var factory = new ModularWebApplicationFactory<Program>("Orders.Entities", "OrdersModule");
```

`ModularWebApplicationFactory<TEntryPoint>` is a `WebApplicationFactory` that sets
`HOSTINGSTARTUPASSEMBLIES` to the names it is given, and nothing else; it works with any test
framework. Keep the module names in one place in the test project: they are the same strings a
deployment writes, and a typo in them fails startup with a message naming the module — when the host
calls `ValidateModules()`.

This is the other half of section 1, not a later step: everything section 2 lists is found here or
in production.

A module's hosted services really run here. `StartAsync` opens the database connection, does the
Redis handshake, subscribes to the queue — and on a machine with none of that, every topology test
hangs or fails for a reason that has nothing to do with composition. The question this test asks is
what composed, not what connected, so take them out and leave the one that builds the pipeline:

```csharp
internal sealed class OfflineTopology(params string[] modules) : ModularWebApplicationFactory<Program>(modules)
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

`WithoutApplicationHostedServices` keeps `GenericWebHostService` because it is what builds the
pipeline, and therefore the route table the test came for. `UseConnectionStrings` and the host's
own startup switch do the rest. Connection strings have to be present and parse even though nothing
connects — Npgsql and the Redis client both connect lazily, so an address nothing is listening on is
enough. It sets each one verbatim, so write each in its own client's format: an Npgsql-style string
handed to EasyNetQ starts the host and fails on the first use of the bus. Port 1 on the loopback
address, per client: `Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused`
(Npgsql), `Server=127.0.0.1,1;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=true;Connect Timeout=1`
(SQL Server), `host=127.0.0.1;port=1` (EasyNetQ), `127.0.0.1:1,abortConnect=false` (Redis). And whatever switch keeps the second
replica from migrating in the compose file turns the startup gate off here too:
`Database:Migrate=false` is the samples' convention — use your host's own key if it has another.

The factory refuses to start when the test process has `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` set:
ASP.NET Core would run those modules beside the ones the test named. Unset it for the test run.

`factory.Services.Routes()` is section 1's inventory for a topology, with the module that serves
each route: `GET /orders/{id} (OrdersModule)`. The module is a loaded module, or `host`
(`ModuleRoute.Host`) for the host's own assembly — whether or not the host is a module too, as the
startup report counts it; a route whose handler is the framework's or a library's — `MapOpenApi`'s document, a health
check — has none, and the startup report does not count it either. Static assets (`MapStaticAssets`,
an endpoint per file and per fingerprinted copy) are left out unless you ask with
`Routes(includeStaticAssets: true)`.

An entry point does not have to be made public for this: `InternalsVisibleTo` for the test project
is enough for `WebApplicationFactory<Program>`, and an alias on the reference tells several
`Program`s apart (section 1).

Run every test project, and the end-to-end suite if there is one, on **every commit** of the
migration and not only on its tip — a detached worktree per commit does it. Topology tests make no
requests, so they stay green on a commit where every functional test fails. And when a result
depends on the environment, run the topology tests in both: container validation
(`ValidateOnBuild`) happens in Development only.

Assert that each topology boots: `NoModules`, each module alone, and the full set. A module that
cannot start alone usually has an undeclared dependency on another module's services, which is
worth knowing before a deployment finds out.

## 5. Two kinds of test, kept apart

**Topology tests** know about modules only as the names handed to the factory, exactly as a
deployment names them. They assert on route inventories and on model shape — table and schema
names — never on entity types. No database: building an EF model needs a provider, not a
connection.

**Business logic tests** do not boot a host at all. They reference the module assemblies directly
like any other library, compose whatever model they need with `UseModules(names)` and
`ApplyModuleConfigurations` over the assemblies they name, and reach internals through
`InternalsVisibleTo` — because MOD0001 means everything worth testing in a module is internal.

Mixing them produces tests that need a host to check a five-line rule, and tests that need module
types to check a topology. Keep the line sharp.

Both kinds hit EF Core's model cache if they compose more than one model from the same context
type. `UseModules` takes care of it, in tests as in the host: its model cache key includes the
module set.

## 6. Model snapshots

`ctx.Model.ToDebugString()` per topology, as golden files. Then
`dotnet ef migrations has-pending-model-changes` against the union.

The inventory of section 1 carries the same thing for every context of every host, plus what the
debug view leaves out — query filters and seed data — and `--compare` reports a model entity type
by entity type, under the headings of the debug view:

```text
ordering-api: 2 differences
  - model Ordering.OrderingContext: Ordering.Order: EntityType / Properties / Total / Annotations / Precision: 18
  + model Ordering.OrderingContext: Ordering.Order: EntityType / Properties / Total / Annotations / Precision: 10
```

That is the check for any refactoring of `OnModelCreating`, MOD0016's fix included
([data.md](data.md)): nothing reported means the model is the same. A filter that moved to another
class is the same filter. `model of … not compared` is a note, not a pass: one of the two
inventories has no model for that context — it was taken by an older version of the tool, or the
context could not be created outside a request (a tenant, say) and `modelError` says why. With
`--merged`, a context type several services had must have in the host every entity type any of them
had, each as it was.

## 7. Collisions

- No configuration key defined twice with different values across modules.
- No non-`TryAdd` registration of the same service type in two modules.
- No duplicate route templates.

All three are silent at runtime; a test is the only place they will be noticed.

## 8. Contracts, for transports that were kept

The remote and local implementations of an interface pass the same suite. The pattern gives you
this for free; it costs one shared test class to collect.

## 9. Before and after, under load

Same script, both shapes, capturing requests per second, p95 and container memory. Required, for
two reasons: "we merged the services and it got slower" has to be catchable, and the performance
argument for doing this at all is worth checking rather than assuming.

Record hardware, tool version and commit alongside the numbers. Performance claims without them
rot into folklore.

Three launches per commit at least, alternating the commits, and the median: one launch per side
has given the opposite conclusion from three. Measure the true starting commit, not a convenient
middle one. Process count, working set and thread count repeat from launch to launch and can be
reported as numbers. CPU time and allocations vary several-fold between launches of the same code:
report them as a tendency or not at all.

## 10. Containers

Every topology in the compose file starts and answers its health probe. `docker compose up` is
part of verification, not a separate activity.
