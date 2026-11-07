# Phase 3 — Data

The phase with the most ways to lose data, so: **the skill writes SQL and plans; the user runs
them.** No `dotnet ef database update`, no `psql`, nothing against a database that is not a
disposable local container. This is not a formality — say it to the user before starting.

## The recipe

The `FusionModules.EntityFrameworkCore` package: a context whose model is composed from the
modules the host loaded, in a few lines of the application's own `DbContext`. The template is
`assets/templates/ModularDbContext.cs`.

```bash
dotnet add package FusionModules.EntityFrameworkCore
```

```csharp
internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyModuleConfigurations(this);
    }
}
```

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
    .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
    .UseModules(builder.Configuration));

builder.Services.AddTransient<DbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
```

Four things there are load-bearing and none of them are obvious.

**`AddTransient<DbContext>`.** Modules depend on `DbContext`, never on the application's context
type. A module needs somewhere to put its tables, not knowledge of the application it is deployed
into. Inside a module: `db.Set<Order>()`.

**`UseModules(builder.Configuration)`, not `AppDomain`.** The modules come from the registry the
modules write as they activate (`ModuleBase.GetLoadedModules`). `AppDomain.CurrentDomain.GetAssemblies()`
filtered by the attribute composes the right model while one host owns the process, and the wrong
one as soon as a second host shares it — in an integration-test assembly every topology's modules
are loaded, so every topology composes the union. Nothing throws; the tables are simply wrong.

**`PendingModelChangesWarning` downgraded to a log.** A topology loading a subset of the modules
legitimately has a smaller model than the migrations snapshot, and `MigrateAsync` treats that as
an error. Downgraded, not suppressed: on the full model it still means something.

**The model cache key.** EF Core caches a built model in an internal service provider shared by
every context with the same options, keyed by context type. Two hosts with different module sets
in one process — every integration test assembly — would share the first one's model. `UseModules`
replaces EF Core's `IModelCacheKeyFactory` with one whose key includes the module set. A context
that replaces `IModelCacheKeyFactory` itself — a schema per tenant, say — replaces this one too,
and its own key has to include `context.GetModuleSetKey()` as well.

Once per set of modules the package logs, in the category `FusionModules.EntityFrameworkCore`, which
modules the model was composed from and in what order, which had no configurations, and which
entities several modules configure. When a topology is missing a table, read that line first.

Without the package — an application that will not take the dependency — the same is a fingerprint
of `GetLoadedModules` on the context, an `IModelCacheKeyFactory` that includes it,
`ReplaceService<IModelCacheKeyFactory, …>()` on the options, and `ApplyConfigurationsFromAssembly`
for each module in `GetLoadedModules` order.

## Where entities go

Into the module that owns them, with their `IEntityTypeConfiguration<T>` beside them. A schema
per module (`entity.ToTable("Orders", "Sales")`) keeps names from colliding and makes ownership
visible in the database.

Split entity modules from feature modules when a topology might want the tables without the
endpoints — a replica that reads a table it does not serve, say. If nothing would ever want that,
one module is fine; do not manufacture the split.

Replace `MyDbContext.Orders` with `db.Set<Order>()`. The trade is real and worth stating: you
lose `DbSet` properties and context-level conventions, and gain a module that can be deployed
without the application it was written for.

## Relationships across modules

Do **not** put a navigation property on one module's entity pointing at another's — that couples
them in every topology.

Instead, a **composing module** declares a second `IEntityTypeConfiguration<T>` for the same
entity, in its own assembly:

```csharp
// BillingModule — owns no entities, joins two other modules'
internal sealed class OrderCustomerConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> entity) =>
        entity.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId);
}
```

Load that module and the foreign key exists; leave it out and the two tables are independent.
Because it uses both modules' types, it references both, so `ModuleBase` will not let a topology
load it without them.

**The composing module's configuration is applied after the ones it extends** — not because of
where `HOSTINGSTARTUPASSEMBLIES` lists it: `ApplyModuleConfigurations` applies the modules in
`GetLoadedModules` order, which is the host's own module first (when the host is one), then each
module after the modules its assembly references, and a composing module uses their types. One set
of modules has one order in every topology and at design time, so it also has one EF Core model.

## Migrations

**Always generated against the union of every module, and applied whole.** A topology that loads
a subset has tables it does not use. Generating a migration per topology gives you a database
whose shape depends on which replica reached it first.

`dotnet ef` builds the host to find the context, so `HostingStartup` runs and the model follows
`HOSTINGSTARTUPASSEMBLIES` as set in that shell. So the schema depends on the machine it was
generated on: unset gives an **empty migration and no error at all**, one topology's value gives
that topology's tables and no error either. The design-time factory names the modules in code, from
the list FusionModules generates out of the host's references — written by hand it is a copy of the
project file, and the day it falls behind the migration is missing tables and says nothing:

```csharp
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .UseModules(KnownModules.Names)
            .Options;

        return new ApplicationDbContext(options);
    }
}
```

`KnownModules.Names` is the modules the host *references*. A host that is itself a module — its
own `[assembly: HostingStartup]` and entity configurations — is not among them: add its name to the
array, or its tables are missing from the migration.

Check the generated migration before going further: if `Up` is empty, no module was named.

Exactly one replica should run migrations. The others take the database as they find it —
`Database__Migrate: "false"` in the compose file, or whatever switch the host uses.

## Work that must finish before the port opens

A service that checked its own schema, or seeded reference data, before `RunAsync` has nowhere
obvious to put that once it is a module. `ModuleBase`'s overrides are all synchronous, and they run
while the container is still being assembled — too early to resolve anything from it.

`AddHostedService` is closer than it looks, and it is worth being accurate about why it is still
not the answer. A module's hosted services are registered before `GenericWebHostService` is, so
their `StartAsync` runs before Kestrel binds: work there genuinely does delay the first request
rather than race it. What it does not give you is **ordering**. Hosted services start in
registration order, which is `HOSTINGSTARTUPASSEMBLIES` order, so a gate that has to precede every
other module's work cannot be expressed from inside one of them. And the guarantee is registration
order rather than a contract — it has moved once already, between hosting models.

`IHostedLifecycleService.StartingAsync` is the contractual version of the same window: it runs
before any hosted service starts, by specification. Reach for it when the work answers only to
itself. Either way the thing runs once per replica of every topology that loads the module, and
that is the decision to record next to the registration.

When ordering across modules matters, let the module declare the work and let the host run it. The
module puts a descriptor in DI; the host enumerates them between `Build()` and `RunAsync()`:

```csharp
services.AddSingleton(typeof(SchemaTarget), SchemaTarget.Required<OrdersDbContext>("ORDERS"));
```

```csharp
var app = builder.Build();

foreach (var target in app.Services.GetServices<SchemaTarget>())
{
    await EnsureSchemaUpToDateAsync(app, target);
}

await app.RunAsync();
```

`SchemaTarget` is your type, in the contracts library both sides already reference — a name for the
logs, a `DbContext` type, and whether its absence from DI is normal. The host stays ignorant of
modules: it enumerates a contract, and what registered it is none of its business. Ordering stays
with the host, which is the whole reason to prefer this over a hosted service, and the failure is
loud: the host is `await`ing, so an exception stops startup instead of being swallowed into a
background task.

## Consolidating existing databases

Three playbooks. **Choose with the user**; this is one of the Phase 0 questions.

**A — Fresh baseline.** Recommended when databases are being merged. Generate one migration
against the union; for existing databases, generate an idempotent script
(`dotnet ef migrations script --idempotent`) and seed `__EFMigrationsHistory` so the baseline is
a no-op against data that already exists. Verify the generated schema matches what is there
before anybody runs anything.

**B — Keep them separate.** Databases and contexts stay as they are; only the process merges.
Give each context its own `MigrationsHistoryTable(name, schema)` so two histories cannot collide
if they ever share a database. Often the right answer, and always the least risky.

**C — Adopt one history as canonical.** Only when one database is clearly the survivor and the
others are being folded into it. Append new migrations to that history.

If data has to move between physical databases, produce the plan — dump, transform, load,
verification queries — and, if downtime is not acceptable, the dual-write plus backfill plus
cutover sequence. Write it; do not run it.

## Gate

- Model snapshot per topology (`ctx.Model.ToDebugString()`) reviewed and stored as a golden file.
- `dotnet ef migrations has-pending-model-changes` clean against the union.
- Integration tests boot each topology and assert its table set.
- Migration scripts reviewed by a human before any database sees them.
