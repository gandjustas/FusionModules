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

Store one file per service, then one per topology, and diff them.

Normalise the leading slash. An attribute route is stored without one and a minimal-API route with
one, so `/orders` and `orders` are the same URL written two ways — and converting a controller to a
minimal API would otherwise diff every route it touched while changing none of them. The template
does this; if you write your own, do it too.

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

## 3. Build

```bash
dotnet build -warnaserror
```

Zero MOD diagnostics. If MOD0003 fires alongside compiler errors, fix the compiler errors first —
Roslyn cannot work out which references are used in a broken compilation, and the analyzer
suppresses itself in that case, so anything you do see is real.

## 4. Boot every topology

```csharp
internal sealed class ModularWebApplicationFactory(params string[] modules) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        base.ConfigureWebHost(builder);
    }
}
```

Ten lines, copied into the test project — there is no package for this, because there is nothing
in it but a setting. Keep the module names in one place in the test project: they are the same
strings a deployment writes, and a typo in them fails startup with a message naming the module.

This is the other half of section 1, not a later step: everything section 2 lists is found here or
in production.

A module's hosted services really run here. `StartAsync` opens the database connection, does the
Redis handshake, subscribes to the queue — and on a machine with none of that, every topology test
hangs or fails for a reason that has nothing to do with composition. The question this test asks is
what composed, not what connected, so take them out and leave the one that builds the pipeline:

```csharp
builder.ConfigureTestServices(services =>
{
    foreach (var descriptor in services
        .Where(d => d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType?.FullName != "Microsoft.AspNetCore.Hosting.GenericWebHostService")
        .ToList())
    {
        services.Remove(descriptor);
    }
});
```

`GenericWebHostService` stays because it is what builds the pipeline, and therefore the route table
the test came for. Two things go with it. Connection strings have to be present and syntactically
valid even though nothing connects — Npgsql and the Redis client both connect lazily, so an address
nothing is listening on is enough. And whatever switch keeps the second replica from migrating in
the compose file turns the startup gate off here too.

Assert that each topology boots: `NoModules`, each module alone, and the full set. A module that
cannot start alone usually has an undeclared dependency on another module's services, which is
worth knowing before a deployment finds out.

## 5. Two kinds of test, kept apart

**Topology tests** know about modules only as the names handed to the factory, exactly as a
deployment names them. They assert on route inventories and on model shape — table and schema
names — never on entity types. No database: building an EF model needs a provider, not a
connection.

**Business logic tests** do not boot a host at all. They reference the module assemblies directly
like any other library, compose whatever model they need by calling
`ApplyConfigurationsFromAssembly` for each assembly they name, and reach internals through
`InternalsVisibleTo` — because MOD0001 means everything worth testing in a module is internal.

Mixing them produces tests that need a host to check a five-line rule, and tests that need module
types to check a topology. Keep the line sharp.

Both kinds hit EF Core's model cache if they compose more than one model from the same context
type. Same fix as the host: an `IModelCacheKeyFactory` that includes what the model was composed
from.

## 6. Model snapshots

`ctx.Model.ToDebugString()` per topology, as golden files. Then
`dotnet ef migrations has-pending-model-changes` against the union.

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

## 10. Containers

Every topology in the compose file starts and answers its health probe. `docker compose up` is
part of verification, not a separate activity.
