# 03 — A modular data model

One EF Core model, assembled from whichever modules are loaded. Three deployment topologies from
one image.

## The modules

| Module | What it contributes |
|---|---|
| `Orders.Entities` | The `Orders` table, in the `Sales` schema |
| `Customers.Entities` | The `Customers` table, in the `Marketing` schema |
| `CustomersModule` | `GET /customers` — a feature module over the customers tables |
| `BillingModule` | `GET /billing/overdue`, and the foreign key from orders to customers |

`BillingModule` is a **composing module**: it owns no entities, it joins two other modules'.
It does that with a second `IEntityTypeConfiguration<Order>`, in its own assembly, adding the
relationship that `Orders.Entities` deliberately does not declare. Load it and the foreign key
exists; leave it out and the two tables are independent.

Which means the composing module must come **last** in `HOSTINGSTARTUPASSEMBLIES` — EF Core
applies configurations in activation order, and this one has to come after the configurations it
extends.

## The recipe

The whole EF Core integration is in [`ApplicationDbContext.cs`](Host/ApplicationDbContext.cs) and
three lines of [`Program.cs`](Host/Program.cs). There is no Modulith EF package, because there is
nothing here worth packaging:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    foreach (var assembly in ModuleBase.GetLoadedModules(configuration))
        modelBuilder.ApplyConfigurationsFromAssembly(assembly);
}
```

Three things around it are less obvious and all three are load-bearing.

**`AddTransient<DbContext>(...)`.** Modules take a dependency on `DbContext`, never on the
application's context type. A module needs somewhere to put its tables, not knowledge of the
application it is deployed into.

**`ConfigureWarnings(w => w.Log(PendingModelChangesWarning))`.** A topology that loads a subset of
the modules legitimately has a smaller model than the migrations snapshot, and `MigrateAsync`
treats that as an error by default. Downgraded to a log entry, not suppressed: on the full model
it still means something.

**`ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>()`.** EF Core caches a
built model in an internal service provider shared by every context with the same options, keyed
by context type. Two hosts with different module sets in one process — which is every integration
test assembly — would otherwise share the first one's model. Nothing throws; you just get the
wrong tables. `ModelCompositionTests.TopologiesInOneProcessDoNotShareAModel` fails without it.

## Migrations

Generated against the **union of every module**, and applied whole:

```bash
cd Host
dotnet ef migrations add Initial
```

A deployment that loads a subset simply has tables it does not use. Generating a migration per
topology would give you a database whose shape depends on which replica reached it first.

`dotnet ef` builds the host to find the context, so `HostingStartup` runs and the model follows
`HOSTINGSTARTUPASSEMBLIES` as it stands in your shell — an **empty migration and no error at all**
when it is unset, one topology's tables when it is not.
[`DesignTimeDbContextFactory`](Host/DesignTimeDbContextFactory.cs) names every module with
`ModuleBase.CreateModuleRegistry`, so the migration is the same on every machine.

## Running it

```bash
docker compose up --build
curl localhost:5011/customers        # the customers topology
curl localhost:5012/billing/overdue  # the billing topology
curl -X POST localhost:5010/billing/seed && curl localhost:5010/billing/overdue
```

Same image in all three, differing only by `HOSTINGSTARTUPASSEMBLIES`. Only one replica runs
migrations; the others take the database as they find it.

## Tests

Two kinds, and the difference is the point.

**[`Tests/`](Tests) — topology.** Modules appear only as the names handed to
`ModularWebApplicationFactory`, exactly as a deployment names them. The assertions are the model's
tables and the route table, never entity types: a test that reaches for a module's types is
testing that module, not the composition. No database is involved — building an EF model needs a
provider, not a connection.

Route presence is read off `EndpointDataSource` rather than probed over HTTP, because an HTTP
probe of an endpoint that queries the database cannot tell "not deployed" from "deployed and
broken". That inventory is also the snapshot worth keeping when migrating an existing service
into a module: a mechanical migration that preserves the route table is very likely correct, and
one that does not gives you a reviewable diff.

**[`UnitTests/`](UnitTests) — business logic, no host.** Nothing goes through
`HOSTINGSTARTUPASSEMBLIES`. The module assemblies are referenced and used like any other library,
the test composes the model itself by calling `ApplyConfigurationsFromAssembly` for each assembly
it names, and the rule under test is reached through `InternalsVisibleTo` — because MOD0001 means
everything worth testing in a module is internal. SQLite in memory: what counts as an overdue
order does not need a database server to answer.

That choice has a visible cost, and it is worth seeing rather than hiding. The SQLite provider
cannot translate `DateTimeOffset` comparisons, so `Order` uses UTC `DateTime`. It is a defensible
domain choice on its own, but it *was* the test provider reaching back into the model, which is
what you pay for not testing against the real one.

The unit tests hit the model cache trap too — same context type, different assembly sets — and
solve it the same way the host does. Two places, one lesson.

`dotnet test` is enough; nothing here needs Docker.
