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

## 2. Build

```bash
dotnet build -warnaserror
```

Zero MOD diagnostics. If MOD0003 fires alongside compiler errors, fix the compiler errors first —
Roslyn cannot work out which references are used in a broken compilation, and the analyzer
suppresses itself in that case, so anything you do see is real.

## 3. Boot every topology

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

Assert that each topology boots: `NoModules`, each module alone, and the full set. A module that
cannot start alone usually has an undeclared dependency on another module's services, which is
worth knowing before a deployment finds out.

## 4. Two kinds of test, kept apart

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

## 5. Model snapshots

`ctx.Model.ToDebugString()` per topology, as golden files. Then
`dotnet ef migrations has-pending-model-changes` against the union.

## 6. Collisions

- No configuration key defined twice with different values across modules.
- No non-`TryAdd` registration of the same service type in two modules.
- No duplicate route templates.

All three are silent at runtime; a test is the only place they will be noticed.

## 7. Contracts, for transports that were kept

The remote and local implementations of an interface pass the same suite. The pattern gives you
this for free; it costs one shared test class to collect.

## 8. Before and after, under load

Same script, both shapes, capturing requests per second, p95 and container memory. Required, for
two reasons: "we merged the services and it got slower" has to be catchable, and the performance
argument for doing this at all is worth checking rather than assuming.

Record hardware, tool version and commit alongside the numbers. Performance claims without them
rot into folklore.

## 9. Containers

Every topology in the compose file starts and answers its health probe. `docker compose up` is
part of verification, not a separate activity.
