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

`dotnet ef` never starts the host, so no module activates and the registry the model is built
from is empty — which produces an **empty migration and no error at all**.
[`DesignTimeDbContextFactory`](Host/DesignTimeDbContextFactory.cs) fills it in with
`ModuleBase.CreateModuleRegistry`.

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

**[`Tests/`](Tests) — integration.** Modules appear only as the names handed to
`ModularWebApplicationFactory`, exactly as they appear in a deployment. The assertions are HTTP
responses and table names, never entity types: a test reaching for a module's types is testing
that module, not the topology.

**[`UnitTests/`](UnitTests) — business logic, no host.** Nothing goes through
`HOSTINGSTARTUPASSEMBLIES`. The module assemblies are referenced and used like any other library,
the test composes the model itself by calling `ApplyConfigurationsFromAssembly` for each assembly
it names, and the rule under test is reached through `InternalsVisibleTo` — because MOD0001 means
everything worth testing in a module is internal.

Both run against a real PostgreSQL through **Testcontainers**, one container per test assembly and
a fresh database per test. The rules under test are expressed in schemas, decimal precision and
query translation; a provider that quietly ignores `ToTable("Orders", "Sales")` would let these
tests agree with a production database they do not describe.

**Docker is required.** Without it these two projects fail to start; the other samples' tests do
not need it.
