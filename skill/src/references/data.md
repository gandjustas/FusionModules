# Phase 3 — Data

The phase with the most ways to lose data, so: **the skill writes SQL and plans; the user runs
them.** No `dotnet ef database update`, no `psql`, nothing against a database that is not a
disposable local container. This is not a formality — say it to the user before starting.

## The recipe

There is no Modulith EF Core package. The integration is about fifteen lines in the application's
own `DbContext`, and wrapping that in a base class to inherit and a method to remember would be
the framework the approach claims not to need.

```csharp
internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
    : DbContext(options)
{
    public string ModuleFingerprint { get; } =
        string.Join(';', ModuleBase.GetLoadedModules(configuration).Select(a => a.GetName().Name));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var assembly in ModuleBase.GetLoadedModules(configuration))
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }
}
```

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
    .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
    .ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>());

builder.Services.AddTransient<DbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
```

Four things there are load-bearing and none of them are obvious.

**`AddTransient<DbContext>`.** Modules depend on `DbContext`, never on the application's context
type. A module needs somewhere to put its tables, not knowledge of the application it is deployed
into. Inside a module: `db.Set<Order>()`.

**`GetLoadedModules`, not `AppDomain`.** `AppDomain.CurrentDomain.GetAssemblies()` reports
assemblies that were referenced but never activated, and every module of every other host in a
test process. It will appear to work and then compose the wrong model.

**`PendingModelChangesWarning` downgraded to a log.** A topology loading a subset of the modules
legitimately has a smaller model than the migrations snapshot, and `MigrateAsync` treats that as
an error. Downgraded, not suppressed: on the full model it still means something.

**`ModuleAwareModelCacheKeyFactory`.** EF Core caches a built model in an internal service
provider shared by every context with the same options, keyed by context type. Two hosts with
different module sets in one process — every integration test assembly — otherwise share the
first one's model. Nothing throws; you get the wrong tables.

```csharp
internal sealed class ModuleAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is ApplicationDbContext app
        ? (typeof(ApplicationDbContext), app.ModuleFingerprint, designTime)
        : (object)(context.GetType(), designTime);
}
```

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

**The composing module goes last in `HOSTINGSTARTUPASSEMBLIES`.** EF Core applies configurations
in activation order, and this one extends configurations declared elsewhere. Write that down in
the compose file, not just in a commit message.

## Migrations

**Always generated against the union of every module, and applied whole.** A topology that loads
a subset has tables it does not use. Generating a migration per topology gives you a database
whose shape depends on which replica reached it first.

`dotnet ef` builds the host to find the context, so `HostingStartup` runs and the model follows
`HOSTINGSTARTUPASSEMBLIES` as set in that shell. So the schema depends on the machine it was
generated on: unset gives an **empty migration and no error at all**, one topology's value gives
that topology's tables and no error either. The design-time factory names the modules in code:

```csharp
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(AllModules))
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>()
            .Options;

        return new ApplicationDbContext(options, configuration);
    }
}
```

Check the generated migration before going further: if `Up` is empty, the registry was not
populated.

Exactly one replica should run migrations. The others take the database as they find it —
`Database__Migrate: "false"` in the compose file, or whatever switch the host uses.

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
