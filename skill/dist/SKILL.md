---
name: modulith
description: >-
  Build or migrate to a modular monolith on ASP.NET Core HostingStartup modules using the Modulith
  package. Use when the user wants to merge .NET microservices into one process or one image, cut
  network hops, latency or infrastructure between services they own, split a monolith into
  independently deployable modules, choose deployment topology with an environment variable
  instead of a rebuild, replace HttpClient/gRPC/RabbitMQ calls between their own services with
  in-process calls, consolidate per-service DbContexts or EF Core migrations, or when they mention
  Modulith, ModuleBase, IHostingStartup, HOSTINGSTARTUPASSEMBLIES, IStartupFilter modules, a
  modular monolith, or "modularity without microservices".
---

# Modular monoliths on ASP.NET Core

A module is an ordinary class library carrying `[assembly: HostingStartup(typeof(Module))]`. It is
activated by naming its assembly in `HOSTINGSTARTUPASSEMBLIES`. One image serves every deployment
topology, and which modules are live is a deployment decision rather than a build decision.

The host has no compile-time knowledge of any module. That is the property everything else rests
on, and the thing to protect when in doubt.

**Read [How it works](#how-it-works) before changing any code.** The mechanism is small
but not obvious, and most mistakes here fail silently rather than loudly.

## Which direction

**Monolith → modules.** The existing `Program.cs` is not touched. Add the package, make sure
`UseRouting()` is there, then move one `app.MapX` block and its registrations into a module at a
time. The application works after every step. This is the low-risk direction and the one to
prefer when both are on the table.

**Microservices → modules.** Start a new empty host rather than promoting one of the services:
it inherits no references, so MOD0003 is green from the first build. Then convert services
lowest-fan-in first.

**New system.** Start from [samples/01-minimal-api](https://github.com/gandjustas/modulith/tree/main/samples/01-minimal-api)
and skip the assessment.

## Phases

Work through these in order. Each has a gate that must pass before the next begins.

| | | |
|---|---|---|
| 0 | [Assess](#phase-0-assess) | Read-only. Produces `modulith-assessment.md` and a list of decisions only a human can make. |
| 1 | [Host](#phase-1-the-host) | A host that serves nothing. Gate: it runs and 404s. |
| 2 | [Modules](#phase-2-into-modules) | One service or feature at a time. Gate: route inventory unchanged, per module. |
| 3 | [Data](#phase-3-data) | Entities into the modules that own them; one model composed at startup. **Never runs destructive database commands.** |
| 4 | [Transports](#phase-4-transports) | Contracts out, in-process implementations in, remote ones kept only where they earn it. |
| 5 | [Deployment](#phase-5-deployment) | One image, topologies as environment variables. |
| 6 | [Verify](#verification) | Run after *every* phase, not at the end. |

State lives in `modulith-migration.md` at the repository root: the phase, the decisions taken and
their reasons, and the status of each service. Write to it as you go, so a resumed session does
not re-ask questions the user has already answered.

## Rules of engagement

**Ask before deciding what only the user can decide.** Module boundaries and module names become
`HOSTINGSTARTUPASSEMBLIES` values, which are a deployment contract and effectively permanent. So
are the answers about which transports to keep and how to consolidate databases. Put the
questions in one numbered batch at the end of Phase 0, record the answers, and do not re-derive
them later.

**Escalate blockers, do not work around them.** See [Assess](#phase-0-assess) for the list.
A service that exists *because* it is isolated — for compliance, for a different scaling profile,
for a different release cadence — does not become a module because merging is technically
possible.

**Never collapse asynchronous messaging silently.** Turning a durable publish into a method call
changes at-least-once into at-most-once and removes retry, dead-lettering, backpressure and
failure isolation. Every messaging edge gets an explicit decision from the user with the
semantics delta written down. See [Transports](#phase-4-transports).

**Never run a destructive database command.** Write the SQL and the plan; the user runs them.
`dotnet ef database update`, `psql`, and anything touching a database that is not a disposable
local container are out of scope for this skill.

**Preserve the route inventory.** It is the strongest available signal that a mechanical
migration is correct. Capture it before touching anything — see
[Verification](#verification).

## When something fails quietly

Nearly every failure mode of this approach is silent: the application starts, the probe passes,
and an endpoint is simply missing. See [Troubleshooting](#when-it-fails-quietly), which
lists them by symptom along with what each MOD diagnostic means.

---

## Phase 0 — Assess

Read-only. Change nothing. The output is `modulith-assessment.md` and one batch of questions.

The risk in this phase is not being wrong, it is being inconsistent — looking at different things
in different repositories and reaching confident conclusions from an incomplete picture. Run the
inventory script first and reason over its output rather than grepping ad hoc.

```bash
pwsh assets/scripts/assess.ps1 -Path <solution-root> -Output modulith-assessment.json
```

Needs `pwsh`, which is cross-platform. Without it, gather the same things by hand — the list
below is the checklist either way.

Three things it does not do, so do not take its silence as an answer:

- **It composes no routes.** `MapGroup("/billing")` followed by `MapGet("/overdue")` is reported
  as two templates, not one. The reliable route inventory comes from `EndpointDataSource` at
  runtime — see [verify.md](verify.md). This list is for spotting collisions early, not for the
  baseline.
- **Its collisions are candidates.** Two modules mapping the same template only matters if a
  topology loads both. Check before raising it.
- **Properties inherited from `Directory.Build.props` are reported separately**, not resolved.
  Resolving them properly means an MSBuild evaluation per project, which turns seconds into
  minutes. So a project whose `targetFramework` is empty probably inherits it.

### What the assessment must contain

**Services.** Project, SDK, target framework, entry point, assembly name, what it serves.

**The call graph between them.** One row per edge: caller, callee, transport (HTTP / gRPC /
queue / database), the contract type, and the call site as `file:line`. This is the input to
Phase 4 and the thing most likely to be incomplete — check service discovery configuration and
compose files as well as code, because an edge can exist entirely in configuration.

**Data.** Per service: `DbContext` types, provider, connection string, migrations assembly and
history table, and whether any two services share a database. Two services on one database is a
different migration than two services on two databases.

**Deployment.** Images, Dockerfiles, compose/Kubernetes/Aspire definitions, CI jobs per service,
replica counts and any autoscaling rules. Replica counts matter: they are the evidence for
whether a service genuinely has its own scaling profile.

**Cross-cutting, as a diff table.** Authentication schemes, authorization policies, CORS, rate
limiting, OpenTelemetry, health checks, problem details, localization — and the middleware order
each service uses. Divergent middleware order is the single largest source of behaviour change
after a merge, and it is invisible in a per-service reading. Lay them side by side.

**Collisions.** Three kinds, all silent:

- *Routes.* Enumerate every template across every service and list the duplicates. Never merge
  two services with a known route collision; resolve it with `MapGroup` or an area first.
- *Configuration keys.* The same key with different values in two services means one of them
  changes behaviour after the merge, and nothing will say so.
- *DI registrations.* A non-`TryAdd` registration of the same service type in two modules means
  the winner is decided by `HOSTINGSTARTUPASSEMBLIES` order. List every interface registered more
  than once.

**Proposed module boundaries and topologies**, with the reasoning.

**Risks**, including the ones below.

### Blockers — escalate, do not work around

Stop and raise these with the user. Some are fatal to the whole idea; all of them change the
plan.

| | |
|---|---|
| Mixed target frameworks | One process, one runtime. Align first or exclude the service. |
| A non-.NET service on the boundary | It stays remote. The question is only which edges change. |
| Owned by another team | Their release cadence becomes yours. That is an organisational decision. |
| Divergent authentication on colliding routes | Merging changes who can reach what. Needs explicit design. |
| Process-wide state or configuration | `ServicePointManager`, thread culture, `AppContext` switches, static caches, process-wide logging configuration. Two services with different settings cannot both be right in one process. |
| Different database engines | Separate contexts and separate migration histories, whatever else happens. |
| Sagas and compensating transactions | The transaction boundaries change. Do not touch these without designing the new boundaries. |
| Different scaling profiles | One CPU-bound service among IO-bound ones is a reason to keep it separate — or to give it a topology of its own. |
| Isolation as the reason it exists | Compliance, blast radius, tenancy. Technically mergeable is not the same as should be merged. |
| Different release cadence or SLA | Merging couples them. Say so before anything else. |
| Background services | A hosted service now runs in every replica of every topology that loads its module. See below. |

#### Background services deserve their own paragraph

A worker that ran once — one replica of one service — now runs wherever its module is loaded. If
two replicas of a merged topology both load it, it runs twice. This changes behaviour silently
and in production. The options are leader election, a dedicated worker topology that is the only
one loading that module, or leaving the service alone. Ask.

### Human gate

End Phase 0 with one numbered batch of questions. Do not start Phase 1 before they are answered,
and record the answers in `modulith-migration.md` with their reasons.

1. **Module boundaries and names.** The names become `HOSTINGSTARTUPASSEMBLIES` values — a
   deployment contract, and effectively permanent. Propose a set; ask for confirmation.
2. **Target topologies.** Which combinations of modules are to be deployable? There is usually
   an all-in one plus the shapes that exist today.
3. **Transports to keep.** Per edge in the call graph. Default to removing edges whose only
   callers are inside this solution, keeping everything else.
4. **Database consolidation.** One database or several; if several, which contexts go where.
   See [data.md](data.md) for the three migration playbooks.
5. **Contract ownership.** For each type that crosses a module boundary: which contracts library
   owns it, and who may change it.

Ask anything else the assessment turned up that has more than one defensible answer. It is
cheaper to ask now than to unpick a decision three phases later.

---

## How it works

Nothing here is a plugin engine. Every mechanism is stock ASP.NET Core, and knowing exactly what
it does is the difference between a migration that works and one that fails in ways nobody can
explain.

### Activation

`WebApplication.CreateBuilder` reads `HOSTINGSTARTUPASSEMBLIES` (semicolon-separated assembly
names; also readable as the `hostingStartupAssemblies` configuration key), prepends the entry
assembly, subtracts `HOSTINGSTARTUPEXCLUDEASSEMBLIES`, and for each name:

1. `Assembly.Load(name)`
2. reads `[assembly: HostingStartup(typeof(X))]`
3. creates an instance of `X`, casts it to `IHostingStartup`, calls `Configure(IWebHostBuilder)`

Three consequences worth holding on to:

- **An assembly with no `HostingStartupAttribute` is loaded and ignored.** No error, no log. This
  is why MOD0005 exists.
- **An assembly that cannot be loaded logs a critical message and startup continues.** The
  application comes up healthy and serves 404s. `ModuleBase` turns this into a startup failure,
  but only if at least one other module did load — see the limit below.
- **Order is the order of the variable**, with the entry assembly first. This is a contract you
  can rely on, and Phase 3 relies on it.

The entry assembly is in that list, so **the host can be a module too**: put
`[assembly: HostingStartup(typeof(Module))]` in the host project and its module runs before the
others. Useful for cross-cutting setup that belongs in every topology.

### ModuleBase

```csharp
[assembly: HostingStartup(typeof(Module))]

sealed class Module : ModuleBase
{
    protected override void ConfigureAppConfiguration(WebHostBuilderContext ctx, IConfigurationBuilder cfg) { }
    protected override void ConfigureServices(WebHostBuilderContext ctx, IServiceCollection services) { }
    protected override void Configure(IApplicationBuilder app) { }
}
```

Deliberately the shape of the classic `Startup` class. `ConfigureServices` runs during host
construction. `Configure` runs after the host has built its own pipeline, through an
`IStartupFilter`, so routing and authentication are already in place — which is why a module maps
endpoints with `app.UseEndpoints(...)` rather than calling `UseRouting` itself.

Do **not** call `IWebHostBuilder.Configure` or `UseStartup` from a module: both replace the
pipeline entirely, discarding everything the host and the other modules set up.

### The registry

Each module writes itself into configuration as it activates:

```
Modulith:Modules:<AssemblyName> = <assembly-qualified name of the module type>
```

`ModuleBase.GetLoadedModules(IConfiguration)` reads it back, in activation order. This is how
anything composed from modules — an EF Core model, a health check, a diagnostic endpoint — finds
out what is live, without the host having to know that modules exist.

**Do not use `AppDomain.CurrentDomain.GetAssemblies()` for this.** It reports whatever happens to
be loaded in the process, which includes assemblies that were referenced but never activated, and
in a test process it reports every module of every host that has run. The registry reports
exactly the modules that ran, in the order they ran.

For code that runs without a host — a `dotnet ef` design-time factory, chiefly — the registry is
empty, because no module ever activated. `ModuleBase.CreateModuleRegistry(params string[])`
builds the entries. Skipping this produces an **empty migration and no error at all**.

### Dependencies between modules

A module that uses another module's types depends on it. Nothing declares that: the compiler only
emits an assembly reference for an assembly whose types are actually used, so the reference list
is exact. `ModuleBase` checks at startup that every referenced module was activated and fails
loudly if one was not.

Which means a reference is a statement about deployment. If module A uses one DTO from module B,
every topology containing A must also contain B. When that is not what you want, the type belongs
in a **contracts library**: a plain `Microsoft.NET.Sdk` project with no `[HostingStartup]`, which
is therefore not a module and whose types are meant to be public.

#### The limit, stated plainly

The "module did not activate" check runs from the modules that *did* activate. If every name in
`HOSTINGSTARTUPASSEMBLIES` is misspelled, nothing loads and nothing notices. Closing that would
mean the host knowing it has modules, which is the property the whole model exists to preserve.
MOD0005 covers the same class of mistake at build time.

### Project shapes

| | |
|---|---|
| Module, code only | `Microsoft.NET.Sdk` — ASP.NET types arrive through the package's framework reference |
| Module with `.cshtml` | `Microsoft.NET.Sdk.Razor`; Modulith sets `AddRazorSupportForMvc`, needed for Razor Pages as much as MVC |
| Host | `Microsoft.NET.Sdk.Web` |
| Contracts | `Microsoft.NET.Sdk`, no `[HostingStartup]`, public types |

The host references its modules:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" ModulithModule="true" />
```

An ordinary project reference — it orders the build and copies the assembly next to the host so
it can be loaded by name. The metadata drives `KnownModules` generation, which turns module names
into compile-checked constants.

**Do not set `ReferenceOutputAssembly="false"`.** It looks like hardening. The analyzer tolerates
it and the module then stops being copied to the output at all, so nothing loads and the failure
presents as a routing problem. MOD0003 is the enforcement mechanism, not MSBuild.

### MVC and Razor Pages

Application Part Discovery does not know about HostingStartup. Left alone, the Razor SDK emits
`[assembly: ApplicationPart("X")]` on the host for every MVC-flavoured project reference, and at
runtime `AddControllers`/`AddRazorPages` load those assemblies — so the host gets a module's
controllers **without ever running the module's code**, in every topology, including the ones
that excluded it.

Modulith sets `GenerateMvcApplicationPartsAssemblyAttributes` to false for hosts. Each module
adds itself instead, inside its own `ConfigureServices`, where it also registers what its
controllers need:

```csharp
services.AddControllersWithViews().AddApplicationPart(typeof(Module).Assembly);
```

Use an MVC area or a `MapGroup` prefix per module so routes from different modules cannot collide.

### What this costs

Modules load by name at runtime, so the trimmer cannot see that they are used:
`PublishTrimmed` and `PublishAot` are not available for the host. Say so before the user finds
out during a deployment.

---

## Phase 3 — Data

The phase with the most ways to lose data, so: **the skill writes SQL and plans; the user runs
them.** No `dotnet ef database update`, no `psql`, nothing against a database that is not a
disposable local container. This is not a formality — say it to the user before starting.

### The recipe

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

### Where entities go

Into the module that owns them, with their `IEntityTypeConfiguration<T>` beside them. A schema
per module (`entity.ToTable("Orders", "Sales")`) keeps names from colliding and makes ownership
visible in the database.

Split entity modules from feature modules when a topology might want the tables without the
endpoints — a replica that reads a table it does not serve, say. If nothing would ever want that,
one module is fine; do not manufacture the split.

Replace `MyDbContext.Orders` with `db.Set<Order>()`. The trade is real and worth stating: you
lose `DbSet` properties and context-level conventions, and gain a module that can be deployed
without the application it was written for.

### Relationships across modules

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

### Migrations

**Always generated against the union of every module, and applied whole.** A topology that loads
a subset has tables it does not use. Generating a migration per topology gives you a database
whose shape depends on which replica reached it first.

`dotnet ef` never starts the host, so no module activates and the registry is empty — which
produces an **empty migration and no error at all**. The design-time factory has to fill it in:

```csharp
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(KnownModules.All))
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

### Consolidating existing databases

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

### Gate

- Model snapshot per topology (`ctx.Model.ToDebugString()`) reviewed and stored as a golden file.
- `dotnet ef migrations has-pending-model-changes` clean against the union.
- Integration tests boot each topology and assert its table set.
- Migration scripts reviewed by a human before any database sees them.

---

## Phase 5 — Deployment

One Dockerfile. One image. As many deployments as there are topologies, differing only by an
environment variable.

```yaml
services:
  monolith:
    image: app
    build: &build { context: ., dockerfile: Host/Dockerfile }
    environment:
      HOSTINGSTARTUPASSEMBLIES: Orders.Entities;Customers.Entities;CustomersModule;BillingModule

  customers:
    image: app
    build: *build
    environment:
      HOSTINGSTARTUPASSEMBLIES: Customers.Entities;CustomersModule
      Database__Migrate: "false"

  billing:
    image: app
    build: *build
    environment:
      # The composing module goes last: its configuration extends the two above.
      HOSTINGSTARTUPASSEMBLIES: Orders.Entities;Customers.Entities;BillingModule
      Database__Migrate: "false"
```

Write the ordering rule as a comment wherever a composing module appears. It is invisible
otherwise, and reordering the list looks harmless.

### What goes away

- per-service Dockerfiles
- the CI build matrix over services
- service discovery configuration for edges that are now in-process
- the service mesh routes, retries and timeouts for those same edges

Delete them in the same commit as the change that makes them redundant, not later. Stale
infrastructure that still works is the hardest kind to remove afterwards.

### What replaces "is the service up"

A topology that loads the wrong modules still starts and still passes a plain liveness probe. Give
readiness something to check:

```csharp
builder.Services.AddHealthChecks().AddCheck("modules", () =>
{
    var expected = builder.Configuration["Deployment:ExpectedModules"]?.Split(';') ?? [];
    var loaded = ModuleBase.GetLoadedModules(builder.Configuration)
        .Select(a => a.GetName().Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missing = expected.Where(m => !loaded.Contains(m)).ToArray();
    return missing.Length == 0
        ? HealthCheckResult.Healthy()
        : HealthCheckResult.Unhealthy($"modules not loaded: {string.Join(", ", missing)}");
});
```

Three lines of the host's own code, not package API. `ModuleBase` already fails startup when a
*named* module cannot be loaded; this catches the other direction — a deployment whose variable
was edited to something that loads fine but is not what this replica is for.

### The Dockerfile

Standard multi-stage. Two things worth doing:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

## Restore against project files alone, so a code change does not invalidate the restore layer.
COPY --parents **/*.csproj Directory.*.props global.json ./
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore Host/Host.csproj

COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Host/Host.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Host.dll"]
```

**Not `PublishAot` or `PublishTrimmed`.** Modules are loaded by name at runtime, so the trimmer
cannot see that they are used and will remove them. If the user was counting on AOT, this is the
moment to say so rather than after the first trimmed image fails to start.

### Aspire

```csharp
builder.AddProject<Projects.Host>("orders")
    .WithEnvironment("HOSTINGSTARTUPASSEMBLIES", "Orders.Entities;OrdersModule");
```

The same project resource, added more than once with different environment variables. Aspire
models this well; it is the same image, and the resource graph says so.

### Gate

- Every topology in the compose file starts and answers its health probe.
- The route inventory of each topology matches what the corresponding service served before.
- No per-service Dockerfile or CI job remains for a service that is now a module.

---

## Phase 1 — The host

The host is the part that must stay ignorant. Everything else is recoverable; a host that knows
about a module has given up the property the whole approach exists for.

### Monolith → modules: do not create one

The existing application *is* the host. Touch two things:

```xml
<PackageReference Include="Modulith" Version="..." />
```

```csharp
app.UseRouting();   // only if it is not already there
```

That is the whole of Phase 1 in this direction. `Program.cs` keeps its services, its middleware
and its endpoints; modules will take them over one at a time, and the application works after
every step. Resist any urge to "clean up the host first" — an empty host at the end is the
result of the migration, not a precondition for it.

### Microservices → modules: a new, empty one

Do not promote one of the services. It carries references to everything it used to call, so
MOD0003 fires on day one and the noise hides real findings. A new project starts green.

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
```

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Modulith" Version="..." />
  </ItemGroup>
</Project>
```

Module references get added in Phase 2, one at a time, as each module appears.

### What belongs in the host

Almost nothing, and the test is: *would every topology want this?*

Genuinely host-level:

- `UseRouting()` — needed when the host maps no endpoints of its own. When it does map some,
  `WebApplication` adds routing anyway; when it does not and a module tries to map endpoints,
  startup throws with a message naming the call. Either way it is cheap to be explicit.
- the composition root for infrastructure the deployment owns rather than the application —
  the database connection string, logging sinks, the OpenTelemetry exporter
- middleware whose order is a property of the application rather than of a feature: exception
  handling, forwarded headers, HTTPS redirection

Not host-level, however tempting:

- authentication and authorization *schemes* used by only some modules
- CORS policies for specific endpoints
- anything registered because one module needs it

When something is needed by every topology but is clearly a feature rather than infrastructure,
make it a module and put it first in every `HOSTINGSTARTUPASSEMBLIES`. The host project can carry
`[assembly: HostingStartup(typeof(Module))]` itself; ASP.NET Core activates the entry assembly
first.

### Cross-cutting concerns, reconciled once

Phase 0 produced a diff table of middleware order and cross-cutting configuration across the
services. Reconcile it now, before any module is converted, and write the decisions down. Doing
it later means re-testing every module that has already moved.

The order of the host pipeline applies to every module. Where two services disagreed, one of them
is going to change behaviour — decide which, deliberately.

### Launch profiles

Add one profile per planned topology, plus `NoModules`. They are the cheapest possible
documentation of what the deployment shapes are, and they keep the topologies exercised during
development rather than only in CI.

```json
{
  "profiles": {
    "NoModules":  { "commandName": "Project" },
    "Orders":     { "commandName": "Project",
                    "environmentVariables": { "HOSTINGSTARTUPASSEMBLIES": "Orders.Entities;OrdersModule" } },
    "AllModules": { "commandName": "Project",
                    "environmentVariables": { "HOSTINGSTARTUPASSEMBLIES": "Orders.Entities;Customers.Entities;OrdersModule" } }
  }
}
```

### Gate

`dotnet run` starts and returns 404 for everything (or, in the monolith direction, behaves
exactly as it did before). `dotnet build -warnaserror` is clean. Only then start Phase 2.

---

## Phase 2 — Into modules

One service or one feature at a time, lowest fan-in first: convert the things nothing else calls,
then the things that called them. Each conversion ends with a passing build, a passing route
inventory and a commit. Never have two half-converted modules at once — when something breaks you
want one suspect.

### The project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>OrdersModule</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Modulith" Version="..." />
  </ItemGroup>
</Project>
```

`Microsoft.NET.Sdk.Razor` instead, if the module ships `.cshtml`.

Set `<AssemblyName>` explicitly. It is the literal string an operator writes into
`HOSTINGSTARTUPASSEMBLIES`, so it must not follow the project file name — renaming a project
would otherwise break every deployment silently.

Then register it with the host and with the test project:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" ModulithModule="true" />
```

Both, not just the host: the metadata is what generates `KnownModules`, and tests are where
module names are written down most often.

### Program.cs into Module.cs

```csharp
[assembly: HostingStartup(typeof(Module))]

namespace OrdersModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        // everything that was builder.Services.*
    }

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints =>
        {
            // everything that was app.MapX
        });
}
```

Middleware the service ran for itself goes at the top of `Configure`, before `UseEndpoints`.
Middleware that was really cross-cutting goes to the host — but only after reconciling it with
the other services, which was Phase 1's job.

### Five collisions, all silent

Work through these for every module. None of them produce an error; all of them change behaviour.

**Routes.** Two modules mapping the same template is decided by load order. Give each module a
`MapGroup` prefix or an MVC area. If the route set genuinely must stay identical to today's, say
so and resolve the collision some other way — but never merge with a known collision outstanding.

**Configuration keys.** The same key with different values in two services: after the merge, one
of them silently gets the other's value. Prefix per module (`Orders:Timeout`), or rename. The
assessment listed these.

**DI registrations.** `services.AddSingleton<IClock, SystemClock>()` in two modules means the
last one wins, and "last" depends on the environment variable. Use `TryAdd*` for anything a
module provides but does not own; for anything genuinely shared, move it to the host or a module
that owns it and let the others depend on it.

**Hosted services.** A worker that ran in one replica of one service now runs in every replica of
every topology loading its module. If this was not already decided in Phase 0, stop and decide it
now — leader election, a dedicated worker topology, or leave it out.

**Static and process-wide state.** Static caches, `AppContext` switches, thread culture. Two
modules disagreeing about these cannot both win.

### Visibility

Build. MOD0001 will report every public type. Apply the "make internal" fix — Fix All handles a
converted service in one pass.

Exempt by default, because the framework finds them by reflection: controllers, page models, view
components, tag helpers, and entity types configured by an `IEntityTypeConfiguration<T>` in the
same assembly.

What remains is the interesting part: a public type with no reason to be public is a leak, and a
public type with a real reason is a **contract**. Move contracts into a plain `Microsoft.NET.Sdk`
library with no `[HostingStartup]` — not a module, so MOD0001 does not apply and its types are
meant to be public. That library is also the answer whenever two modules need to share a type
without one depending on the other's deployment.

For the genuine exceptions the analyzer cannot infer — a DTO bound by a source-generated
`JsonSerializerContext` elsewhere, a SignalR hub, a message contract a broker discovers —
use `.editorconfig`:

```ini
modulith_allowed_public_types = OrderDto, PaymentHub
```

Write down why. Nobody can tell a considered exception from an abandoned one.

### Views and static assets

A module ships its own `Areas/`, `Pages/` and `wwwroot/`. It adds itself as an application part
inside `ConfigureServices`; the host must not, and Modulith turns off the SDK behaviour that
would have done it silently. Static assets are served from `_content/<AssemblyName>/`, so two
modules can both ship `site.css`.

### Per-module gate

Before moving to the next module:

1. `dotnet build -warnaserror` — no MOD diagnostics.
2. The route inventory for the topology containing this module matches what the service served
   before. See [verify.md](verify.md).
3. One smoke test for the module alone, and one for the full topology.
4. Commit.

---

## Phase 4 — Transports

For each edge in the call graph from Phase 0: does it still need to be a network call?

### The pattern

1. **The contract leaves both services.** A plain `Microsoft.NET.Sdk` library, public interface,
   no `[HostingStartup]` — so it is not a module, MOD0001 does not apply, and both sides may
   reference it without either depending on the other's deployment.

   ```csharp
   // Contracts
   public interface IPricing
   {
       Task<Money> QuoteAsync(OrderId order, CancellationToken cancellationToken);
   }
   ```

2. **A `Local` module registers the direct implementation.**

   ```csharp
   protected override void ConfigureServices(WebHostBuilderContext ctx, IServiceCollection services) =>
       services.AddScoped<IPricing, Pricing>();
   ```

3. **Optionally, a `Remote` module keeps the old transport** behind the same interface — the
   `HttpClient`, gRPC or queue client implementation, moved out of the caller.

4. **The caller depends only on the interface.** Which implementation it gets is a topology
   decision, made by `HOSTINGSTARTUPASSEMBLIES`, and the caller's code does not change between
   them.

That last point is what makes this reversible. A topology can be flipped back to remote without
touching a line of business logic, which is worth having the first time the merge turns out to
have been a mistake for one particular edge.

### Keep the remote transport when

- another team or another runtime consumes it — the contract is public whatever you do internally
- it needs to scale independently, and Phase 0's replica counts back that up
- the queue's durability, retry, dead-lettering or backpressure is doing real work
- it is the boundary of a bulkhead you actually want

### Drop it when

- the only callers are services in this solution, and
- there is no scaling or isolation argument, and
- nothing downstream depends on the asynchrony

### Messaging is not a method call

This is the part to slow down on. Replacing a durable publish with an in-process call changes,
all at once:

| | before | after |
|---|---|---|
| delivery | at-least-once | at-most-once |
| failure | retried, eventually dead-lettered | the caller's exception |
| backpressure | the queue absorbs it | the caller waits |
| ordering | per partition or queue | call order |
| isolation | consumer down ≠ producer down | same process, same fate |
| transaction | separate; often an outbox | possibly the same one |

Some of those changes are the point — an outbox that existed only to make a local write and a
remote publish atomic may become unnecessary once both are local. Others are regressions that
will not show up until something fails in production.

**Every messaging edge gets an explicit decision from the user, with the delta written into
`modulith-migration.md`.** Never collapse a queue because it is technically possible.

### Failure handling after the merge

An in-process call cannot time out the way a network call could. Go through what the caller did
about failure:

- **Retries** around a local call re-run a local method. Usually pointless; occasionally harmful.
  Remove them, or move them to where the real I/O now is.
- **Timeouts** still mean something if the callee does I/O. Keep those; drop the ones that were
  guarding the network hop itself.
- **Circuit breakers** around a local call do nothing useful. Remove them and say so.
- **Fallbacks** that returned degraded results when a service was unreachable now trigger only on
  genuine faults. Check that the fallback still makes sense.
- **Blast radius grows.** A module that used to take itself down now takes the process down.
  Note it in the risk register; a topology can be the answer if a module really is that risky.

### Gate

- Both implementations of a retained contract pass the same test suite. One interface, one set of
  expectations — a property the pattern gives you for free, so use it.
- Route inventory unchanged.
- The `modulith-migration.md` entry for each edge says what was decided and why.

---

## When it fails quietly

Almost every failure mode of this approach is silent. The application starts, the probe passes,
and something is simply missing. Work from the symptom.

### An endpoint returns 404 in a topology that should have it

In order of likelihood:

1. **The module's assembly is not in `HOSTINGSTARTUPASSEMBLIES`.** Check the actual environment
   variable in the running container, not the compose file you think it came from.
2. **The module has no `[assembly: HostingStartup(typeof(...))]`.** ASP.NET Core loads the
   assembly, finds no attribute, and moves on without a word. MOD0005 catches this at build time —
   if it did not fire, the build was not run with the analyzers.
3. **The module's `Configure` maps into a group or area whose prefix you have forgotten.**
4. **Two modules mapped the same template** and load order decided it.

Routing is not on this list. A host with no routing does not serve 404s — startup throws, naming
`UseRouting` in the message. See below.

Confirm what actually loaded rather than reasoning about it:

```csharp
ModuleBase.GetLoadedModules(configuration).Select(a => a.GetName().Name)
```

### Startup throws: "EndpointRoutingMiddleware ... must be added ... before EndpointMiddleware"

The host never called `UseRouting()` and maps no endpoints of its own, so `WebApplication` had no
reason to add routing automatically. Add `app.UseRouting()`.

Loud rather than silent, which is why there is no analyzer rule for it: the runtime's message
already names the fix, and a rule would fire on every host that maps an endpoint itself and
therefore does not need the call.

### The application starts but a module's services are missing

The module activated but `ConfigureServices` did not register what the endpoint needs — usually
because the registration stayed behind in the original `Program.cs`. Compare against the service
list captured in Phase 0.

If the endpoint exists and throws on resolution, that is this. If the endpoint does not exist at
all, it is the previous section.

### A migration comes out empty

`dotnet ef` never starts the host, so no module activates, so the registry the model is built
from is empty. The design-time factory must populate it with
`ModuleBase.CreateModuleRegistry(...)`. There is no error — `Up` is just empty. See
[data.md](data.md).

### A topology has tables it should not, or is missing tables it should have

EF Core's model cache, keyed by context type, shared across hosts in one process. The second
topology in a test run gets the first one's model. Nothing throws. Fix with an
`IModelCacheKeyFactory` that includes the module set — [data.md](data.md).

If it happens at runtime rather than in tests, check that the model is being composed from
`GetLoadedModules` and not from `AppDomain.CurrentDomain.GetAssemblies()`.

### Controllers or pages appear in a topology that excluded their module

Application Part Discovery. The host has
`GenerateMvcApplicationPartsAssemblyAttributes` unset or true, so the SDK wired the module's
controllers in at build time, behind `HOSTINGSTARTUPASSEMBLIES`' back. MOD0004 reports it; the
fix is the property plus each module calling `AddApplicationPart` for itself.

### Startup fails: "requires X, which was not activated"

Correct behaviour. The module uses types from another module, so it depends on it. Either add
that module to the topology, or — if the dependency is only a shared DTO — move the type into a
contracts library so the reference goes away.

Do not silence this by removing the project reference: the module would then fail to load at all.

### Startup fails: "listed in HOSTINGSTARTUPASSEMBLIES but could not be loaded"

A typo, or the assembly is not deployed next to the host. Check for
`ReferenceOutputAssembly="false"` on the project reference — it stops the module being copied to
the output, which looks like a routing problem until you look in the folder.

### Nothing at all happens and no module loads

If *every* name in the variable is wrong, there is no module left to notice, and the check cannot
run. Look at the log for ASP.NET Core's own critical message about the first name it could not
load.

### Behaviour changed after merging, in a way nobody can pin down

The usual suspects, in order:

1. **Middleware order.** Each service had its own; now there is one. Phase 0's diff table says
   which ones differed.
2. **A configuration key defined by two modules** with different values. Last write wins.
3. **A non-`TryAdd` DI registration in two modules.** The winner depends on the order of the
   environment variable, which means it can differ between topologies.
4. **A hosted service now running in more replicas than before**, or more than once per topology.
5. **A retry or circuit breaker around what is now a local call**, changing failure behaviour.

### The diagnostics

| | |
|---|---|
| MOD0001 | A module must not expose public types. Make it internal, or move it to a contracts library, or allow it in `.editorconfig` with a reason. |
| MOD0002 | The type in `[assembly: HostingStartup]` must derive from `ModuleBase`. |
| MOD0003 | The host uses a module's types. Move the type to a contracts library. Keep the project reference — the model needs it. |
| MOD0004 | The host declares an `[ApplicationPart]` for a module. Set `GenerateMvcApplicationPartsAssemblyAttributes` to false. |
| MOD0005 | A module nothing names in `[assembly: HostingStartup]`. It would load and do nothing. |
| MOD0006 | A module calls `IWebHostBuilder.Configure` or `UseStartup`, which replace the pipeline rather than add to it. Override `ModuleBase.Configure`. |
| MOD0007 | `ModuleBase` already registers the module as an `IStartupFilter`. Registering it again runs `Configure` twice. |
| MOD0008 | A hosted service in a module runs in every replica of every topology that loads it. Decide how many times it should run, then suppress. |
| MOD0020 | The Modulith package is not referenced, so the rules that need `ModuleBase` are inactive and the build is green because nothing is being checked. |

Full text for each: `docs/rules/MOD0001.md` and siblings in the repository.

---

## Verification

Run this after every phase, not at the end. A migration that is verified only once is a migration
whose failures all arrive together.

### 1. Capture the baseline first

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

### 2. Build

```bash
dotnet build -warnaserror
```

Zero MOD diagnostics. If MOD0003 fires alongside compiler errors, fix the compiler errors first —
Roslyn cannot work out which references are used in a broken compilation, and the analyzer
suppresses itself in that case, so anything you do see is real.

### 3. Boot every topology

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
in it but a setting. Use `KnownModules` for the names so a rename is a compile error.

Assert that each topology boots: `NoModules`, each module alone, and the full set. A module that
cannot start alone usually has an undeclared dependency on another module's services, which is
worth knowing before a deployment finds out.

### 4. Two kinds of test, kept apart

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

### 5. Model snapshots

`ctx.Model.ToDebugString()` per topology, as golden files. Then
`dotnet ef migrations has-pending-model-changes` against the union.

### 6. Collisions

- No configuration key defined twice with different values across modules.
- No non-`TryAdd` registration of the same service type in two modules.
- No duplicate route templates.

All three are silent at runtime; a test is the only place they will be noticed.

### 7. Contracts, for transports that were kept

The remote and local implementations of an interface pass the same suite. The pattern gives you
this for free; it costs one shared test class to collect.

### 8. Before and after, under load

Same script, both shapes, capturing requests per second, p95 and container memory. Required, for
two reasons: "we merged the services and it got slower" has to be catchable, and the performance
argument for doing this at all is worth checking rather than assuming.

Record hardware, tool version and commit alongside the numbers. Performance claims without them
rot into folklore.

### 9. Containers

Every topology in the compose file starts and answers its health probe. `docker compose up` is
part of verification, not a separate activity.
