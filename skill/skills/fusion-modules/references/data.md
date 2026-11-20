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

## One scoped context where there were several

Services that each had a context per request now share one per scope, and two habits break on it:

- **Calls that used to be separate HTTP requests** and are now method calls made in parallel from
  one page hit one context: "A second operation was started on this context instance". An
  in-process implementation of a contract creates a scope per call.
- **A service that disposes the context it was given** disposes everyone's.

More than twenty context types in one process trips EF Core's `ManyServiceProvidersCreatedWarning`;
it is a warning about options built per context, not about modules.

## Where entities go

Into the module that owns them, with their `IEntityTypeConfiguration<T>` beside them. A schema
per module (`entity.ToTable("Orders", "Sales")`) keeps names from colliding and makes ownership
visible in the database.

Split entity modules from feature modules when a topology might want the tables without the
endpoints — a replica that reads a table it does not serve, say. If nothing would ever want that,
one module is fine; do not manufacture the split.

A module reaches its tables through `DbContext`: `db.Set<Order>()`, or an extension member that
reads the same as the property did (below). The trade is real and worth stating: you lose
context-level conventions and the context's own list of what it holds, and gain a module that can
be deployed without the application it was written for.

## Taking a context apart: MOD0016

A service's context usually configures its entities in `OnModelCreating` and names each in a
`DbSet<T>` property. Neither can move with an entity: configuration written in a context belongs to
that context, and a property on the host's shared context is a use of a module's type (MOD0004).
MOD0016 (Info) marks such a context and its fix does the split:

```bash
dotnet modules inventory Shop.slnx -o before.json
dotnet format analyzers Shop.slnx --diagnostics MOD0016 --severity info
dotnet modules inventory Shop.slnx -o after.json --compare before.json
```

- Each entity's configuration becomes `internal sealed class OrderConfiguration :
  IEntityTypeConfiguration<Order>` in a file of its own — the form `ApplyModuleConfigurations`
  finds — and `OnModelCreating` applies it where the configuration stood
  (`modelBuilder.ApplyConfiguration(new OrderConfiguration())`), so the order between entities is
  kept. A context that already scans its assembly, or composes from modules in a module's assembly,
  gets nothing added: the scan finds the classes.
- Each `DbSet<Order> Orders` becomes an extension member of `DbContext` in
  `<Context>Extensions.cs`, and the entity's class gets `ToTable("Orders")` — the property was what
  named the table. On C# 14 `context.Orders` stays as written; before it, every use becomes
  `context.Orders()`. Inside an expression tree it is rewritten to `context.Set<Order>()` either
  way.

Give it the **solution**, as for MOD0015 and for a stronger reason: the properties are read by
projects that reference the context's, and before C# 14 those uses have to be rewritten.
`--severity info` is required here too.

The comparison is the gate. The inventory records every context's model, entity type by entity type
— table, properties, keys, indexes, relationships, query filters, seed data — and every host "the
same" means the fix changed none of it. Do not accept the result on a green build alone.

The message says what stays and why; all of it is deliberate, and none of it should be forced
through by hand without the same comparison:

- **A filter or condition that reads the context** — a tenant filter on `_tenantId`,
  `Database.IsSqlite()`. EF Core evaluates the filter against the context of each query; handed to a
  class's constructor the value would be read once and cached with the model. Written as a statement
  of its own it stays and the rest of the entity moves.
- **Configuration inside an `if` or a loop**, through a local variable, or among `#if`.
- **Anything that would cross a statement that stays** — a loop over
  `modelBuilder.Model.GetEntityTypes()` sees the entities configured before it and not those after.
- **A property an interface asks for** (`IApplicationDbContext`), a `virtual` one, one that is
  assigned or named by `nameof`.
- **A property whose name another context's member already has** — two contexts in two namespaces
  with a `Players` each. The members are on `DbContext`, so a file that imports both namespaces
  could not say which it means (CS9339). The first context reached gets its member and the second
  keeps its property, where some read would see both. The message does not say this one: only the
  fix reads the uses, and the context is still reported afterwards.

A context the rule says nothing about has nothing to move, and that is common in a codebase that
was already tidy: `OnModelCreating` applies configuration classes by name, or calls only `base`.
Silence is not a failure of the run.

What the fix cannot see, so check it: `.cshtml` and `.razor` files that read the property (before
C# 14 they need their parentheses by hand), reflection over the context's `DbSet` properties, and
code outside the solution compiled against a public context.

**One consequence to decide before running it in a module.** Anything that scans the assembly finds
the new classes. A module that keeps a private context of its own, in a host whose shared context
calls `ApplyModuleConfigurations`, will have its entities appear in the shared model as well. That
is where playbook A is heading and it is wrong for playbook B (below) — there, leave the module's
context as it is, or keep its configuration out of a module assembly.

The classes are then ready for the step this section is about: move `OrderConfiguration.cs` and
`Order` to the module that owns them, and when the last one has left, the context's
`OnModelCreating` is `modelBuilder.ApplyModuleConfigurations(this)`.

## One table, one owner: MOD0017

Every table, and every entity, belongs to the one module that names it with `ToTable` (or `[Table]`).
When the services move in, look for the places where two of them map one table: a reporting service
with its own copy of `Order` on `Sales.Orders`, two services each configuring a shared `Order`
class with a `ToTable` of its own. Separate processes let that pass; one host does not:

- **MOD0017** (error, in the host's build) reports a table or an entity that two modules the host
  references name with constants. Alternatives that no topology loads together are listed in
  `fusion_modules_allowed_table_collisions`.
- **The model can refuse it too**, where the context's options add `.CheckModuleTableOwners()`
  after `UseModules`: checked when EF Core finalizes the model, with what the build cannot see —
  tables named by convention, names read from configuration, the schema the finished model gives a
  table. Off by default; turn it on in hosts whose tables are not all named with constants.

Decide with the user which module owns the table, then pick one, in this order of preference:

1. **The other module reads through the owner's contract** — a service in a contracts library, an
   endpoint, or a copy kept from the owner's events. Its entity and its `ToTable` go away.
2. **The entity moves to the owner**, and the other module references the owner's assembly and
   configures what it adds — a relationship, as below — without naming the table.
3. **The other module reads through a view or a query**: `ToView("OrderSummaries", "Sales")` or
   `ToSqlQuery(...)`. A view is not a table it owns; the owner creates it in its migrations and
   keeps it compatible.

Do not silence it by listing the table: the list is for alternatives, and if they ever load
together the model is refused anyway. Several services that really write one table are one module,
or the table is split between them first.

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

`dotnet ef` pointed at a module that became a library finds no way to create its context
("Unable to resolve service for type 'DbContextOptions…'"): the registration is in a module, and
nothing ran it. Point it at the host (`--startup-project`), or give the module's context a
design-time factory of its own while it keeps one.

Exactly one replica should run migrations. The others take the database as they find it —
`Database__Migrate: "false"` in the compose file, or whatever switch the host uses.

## Work that must finish before the port opens

A service that checked its own schema, or seeded reference data, before `RunAsync` has nowhere
obvious to put that once it is a module. `ModuleBase`'s overrides are all synchronous, and they run
while the container is still being assembled — too early to resolve anything from it.

The methods that did this work usually take `this WebApplication`. Retype them to `IHost` or
`IServiceProvider` — and note that a method several services share changes for all of them. What
was synchronous over asynchronous stays so unless you change it; a `.GetAwaiter()` without
`GetResult()` was already fire-and-forget. Register the work first in the module, before services
whose constructors read the tables. And before blaming the move for a pending-model-changes
warning, run the original service against an empty database: it may be the solution's own.

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

With a schema per module on an existing database, never point a module at the old database before
its data has moved: the migrations history is looked up in the module's schema, is not found, and
the tables are created again beside the old ones. Under Aspire, `WaitFor` on a migration resource
waits for it to start; `WaitForCompletion` waits for it to finish.

If data has to move between physical databases, produce the plan — dump, transform, load,
verification queries — and, if downtime is not acceptable, the dual-write plus backfill plus
cutover sequence. Write it; do not run it.

## Gate

- Model snapshot per topology (`ctx.Model.ToDebugString()`) reviewed and stored as a golden file.
- After any change to `OnModelCreating` — MOD0016's fix or a hand-made one — the inventory compared
  with the one taken before: every host "the same".
- `dotnet ef migrations has-pending-model-changes` clean against the union.
- Integration tests boot each topology and assert its table set.
- Migration scripts reviewed by a human before any database sees them.
