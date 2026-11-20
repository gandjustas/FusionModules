---
name: fusion-modules
description: >-
  Build or migrate to a modular monolith on ASP.NET Core HostingStartup modules using the
  FusionModules package. Use when the user wants to merge .NET microservices into one process or
  one image, cut network hops, latency or infrastructure between services they own, split a
  monolith into independently deployable modules, choose deployment topology with an environment
  variable instead of a rebuild, replace HttpClient/gRPC/RabbitMQ calls between their own services
  with in-process calls, consolidate per-service DbContexts or EF Core migrations, or when they
  mention FusionModules, ModuleBase, IHostingStartup, HOSTINGSTARTUPASSEMBLIES, IStartupFilter
  modules, a modular monolith, or "modularity without microservices".
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
it inherits no references, so MOD0004 is green from the first build. Then convert services
lowest-fan-in first. Each service becomes a module inside its own project before it joins anything:
MOD0015's fix writes the `Module.cs`, and the service keeps running alone
([Modules](#phase-2-into-modules)). MOD0016's does the same for a `DbContext`
([Data](#phase-3-data)).

The order that has held up on real solutions, each step leaving everything green:

1. `dotnet modules inventory` before any edit, and its facts frozen into tests.
2. MOD0015 on every service, each still an executable with its own `Program.cs`; `compare`.
3. `dotnet modules topology` on that inventory: what one host would fail on is the work list.
4. The services into the host, **still calling each other over the network** — point each
   client's address at the host itself. Removing a hop and moving a service in one step leaves two
   suspects for every failure.
5. Projects become libraries; MOD0001 and its neighbours arrive now, all at once.
6. In-process contracts in place of the loopback calls, one edge at a time.
7. `compare --merged`: every difference explained, no endpoint lost.

Move the service that owns authentication first. Nearly every runtime failure of a merge comes
with it — default schemes, cookies, issuer addresses — and they are cheaper to meet while it is the
only thing that moved.

**New system.** Start from [samples/01-minimal-api](https://github.com/gandjustas/FusionModules/tree/HEAD/samples/01-minimal-api)
and skip the assessment.

## Phases

Work through these in order. Each has a gate that must pass before the next begins.

| | | |
|---|---|---|
| 0 | [Assess](#phase-0-assess) | Read-only. Produces `fusion-modules-assessment.md` and a list of decisions only a human can make. |
| 1 | [Host](#phase-1-the-host) | A host that serves nothing. Gate: it runs and 404s. |
| 2 | [Modules](#phase-2-into-modules) | One service or feature at a time. Gate: route inventory unchanged, per module. |
| 3 | [Data](#phase-3-data) | Entities into the modules that own them; one model composed at startup. **Never runs destructive database commands.** |
| 4 | [Transports](#phase-4-transports) | Contracts out, in-process implementations in, remote ones kept only where they earn it. |
| 5 | [Deployment](#phase-5-deployment) | One image, topologies as environment variables. |
| 6 | [Verify](#verification) | Run after *every* phase, not at the end. |

State lives in `fusion-modules-migration.md` at the repository root: the phase, the decisions taken and
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

**Preserve the route inventory — and do not stop at it.** It is the strongest available signal
that a mechanical migration is correct, and it is blind to everything resolved by reflection,
negotiated on the wire, or built after the container is. Capture it before touching anything, and
treat booting every topology with one real request per module as its peer rather than its
follow-up. See [Verification](#verification).

## When something fails quietly

Nearly every failure mode of this approach is silent: the application starts, the probe passes,
and an endpoint is simply missing. See [Troubleshooting](#when-it-fails-quietly), which
lists them by symptom along with what each MOD diagnostic means.

---

## Phase 0 — Assess

Read-only. Change nothing. The output is `fusion-modules-assessment.md` and one batch of questions.

The risk in this phase is not being wrong, it is being inconsistent — looking at different things
in different repositories and reaching confident conclusions from an incomplete picture. The
checklist below is what to look at, every time.

### Start from the inventory

Collect the facts with one command, then spend the reading on what it cannot see:

```bash
dotnet tool install --global FusionModules.Tool        # once; or a local tool manifest:
                                                       # dotnet new tool-manifest && dotnet tool install FusionModules.Tool
dotnet modules inventory <solution-or-project> --output fusion-modules-inventory.json
```

It changes nothing in the solution: restore and build go to a directory under the system temp
directory. It finds the deployment the way a person would — the Aspire AppHost, else the compose
files beside the solution, else every executable project that is not a test — evaluates every
project with MSBuild, and then builds each service and captures its host without running its
infrastructure, the way `dotnet ef` does: the DI registrations, hosted services, `DbContext`s and
the endpoints as `EndpointDataSource` has them, each with the assembly that owns it. The JSON is
described in full in the repository's `docs/inventory.md`. Where it goes in the assessment:

| Assessment section | From the inventory |
|---|---|
| Services | `deployment.services` (kind `project`), `projects[]` — SDK, `targetFrameworks`, `assemblyName`, `kind` |
| Call graph | `deployment.services[].references` and `environment`: the edges that exist in configuration (`services__basket-api__http__0`, `ConnectionStrings__eventbus`, `depends_on`). The call sites, `file:line`, still come from reading. |
| Data | `hosts[].runtime.dbContexts` — provider, connection string (passwords masked), migrations assembly and history table. Two services with one database name on one server share a database. |
| Deployment | `deployment` — images, Dockerfiles, `replicas`, ports; infrastructure (`kind: infrastructure`, `type: postgres`…) |
| Background services | `hosts[].runtime.hostedServices` with `application: true` |
| Mixed target frameworks | `projects[].targetFrameworks` of the hosts |
| Collisions | `collisions.routes`, `collisions.configurationKeys`, `collisions.services` |
| What crosses by reflection | `hosts[].runtime.endpoints` of kind `hub` and `grpc` name their types; the rest is reading |

Three things to know before trusting it:

- **Read `errors` and every host whose `runtime.status` is not `started`.** A host that waits for a
  database between `Build()` and `Run()` is reported as `built` or `timeout`: its registrations are
  there, its endpoints are not. Inventory those endpoints by reading, and say so in the assessment.
- **Fakes are marked.** Connection strings and endpoints the deployment does not spell out are
  invented so the host can register its clients; `hosts[].fakedEnvironment` names them. A
  `DbContext` connection string built from one tells you the provider, not the database.
- **Collisions are candidates.** A route served by two services only collides if some topology
  loads both; `sharedHandler: true` marks the ones a shared library maps everywhere (`/health`),
  which the host should map once rather than resolve. Confirm before raising.

- **`built` is the ordinary status** of a host that migrates or seeds between `Build()` and
  `Run()` with no database to reach, and the run still exits 0. On a solution where most services
  do that, most hosts are `built`. But look at `runtime.endpoints` of each: where it is null — a
  gRPC-only service that migrates in `Startup.Configure` — a later `topology` has nothing to compare
  for that host, and its "nothing" is not a clean result.
- **Every web project should be among `hosts`.** One can be missing: a gateway whose compose
  service has another name, a host that reads a relative path and fails under the probe.
  `deployment.notes` says where the deployment was read from — an AppHost that does not build makes
  the tool fall back to compose files, and the environment in the snapshot is then theirs.
- **Check the target frameworks first.** The package has nothing for a project below .NET 8, and a
  worker on `Host.CreateApplicationBuilder` runs no hosting startup. Both are services that stay
  out, whatever else is decided.
- **`git status` afterwards.** The tool writes nothing, but building the solution can: generated
  gRPC clients with other line endings, an OpenAPI document. Restore them.
- **Name the file and restore the tool.** A folder with both a `.slnx` and a `.slnf` needs the one
  you mean. With a tool manifest, `dotnet tool restore` first: without it the command prints a hint
  to run it, and a redirected output file is left empty.
- **It probes in Development** unless the deployment says otherwise, so it lists what only
  Development serves — `/health`, an API reference UI, a redirect from `/`. Take a second one with
  `--environment Production` when the tests you will freeze it into run there, and compare like
  with like.

Then ask it what one host would fail on, before there is one:

```bash
dotnet modules topology fusion-modules-inventory.json --pairs
dotnet modules topology fusion-modules-inventory.json catalog-api basket-api ordering-api
```

The first says which pairs merge cleanly, which is where to start. The second is the work list for
a host composing those services: the service two of them register differently (startup would
fail), the routes more than one maps, the configuration keys with two values. Put it in the
assessment as it is; exit code 2 means the host would not start.

What it reports has nearly always been real. Three kinds of entry are worth a second look before
they go into the plan: a route "mapped twice" that API versioning tells apart, a health check name
that shared service defaults register in every service (the host ends up with one), and "modules
add to one sequence" for a handler or validator interface before the services are converted — run
it again after MOD0015.

"Nothing collides" is not a pass, and neither is exit code 0. It does not see middleware order,
the call sites behind an HTTP or queue edge, a type that crosses an assembly boundary by
reflection, or what a service assumed about being the process — the entry assembly, the content
root, the default authentication scheme ([modules.md](#phase-2-into-modules)). Nor, among things that have
broken real merges:

- **what options say**: a JWT `Audience` per service, `NameClaimType` (`name` in two services,
  `sub` in four), a global MVC filter, Swagger document names, health check names;
- **`Kestrel:EndpointDefaults:Protocols`** set in one service only;
- **registrations made inside a library's factory** — two default document stores, two buses — and
  singletons registered as instances;
- **static state**, such as a change to the default inbound claim type map;
- **a configuration key whose name ends in `key`**: its value is masked and it drops out of the
  printed collisions. Read `collisions.configurationKeys` in the JSON.

Those are what the reading below is for.

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

**What crosses an assembly boundary by reflection.** One list, and the cheapest question in this
phase. SignalR hubs *and the client interface of every `Hub<TClient>`*, `JsonSerializerContext`
types and what they bind, gRPC service bases, `ActivatorUtilities` over a type named in
configuration, anything an assembly scan picks up, anything resolved by a string from a settings
file. Each one is a type whose visibility stops being a free choice when its service becomes a
module, and none of them fail at build time — MOD0001 will tell you to make them internal and the
compiler will agree. Phase 2 spends this list; collect it while you are already reading the code.

**Collisions.** Three kinds, all silent:

- *Routes.* Enumerate every template across every service and list the duplicates —
  `collisions.routes` has them from the endpoints themselves. Never merge two services with a known
  route collision; resolve it with `MapGroup` or an area first.
- *Configuration keys.* The same key with different values in two services means one of them
  changes behaviour after the merge, and nothing will say so.
- *DI registrations.* A non-`TryAdd` registration of the same service type in two modules means
  the winner is decided by `HOSTINGSTARTUPASSEMBLIES` order. List every interface registered more
  than once — `collisions.services` lists the application types two services implement differently.

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
and record the answers in `fusion-modules-migration.md` with their reasons.

1. **Module boundaries and names.** The names become `HOSTINGSTARTUPASSEMBLIES` values — a
   deployment contract, and effectively permanent. Propose a set; ask for confirmation.
2. **Target topologies.** Which combinations of modules are to be deployable? There is usually
   an all-in one plus the shapes that exist today.
3. **Transports to keep.** Per edge in the call graph. Default to removing edges whose only
   callers are inside this solution, keeping everything else.
4. **Database consolidation.** One database or several; if several, which contexts go where.
   See [data.md](#phase-3-data) for the three migration playbooks.
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
  is why MOD0003 exists.
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
    protected override void ConfigureServices(IHostApplicationBuilder builder) { }
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
FusionModules:Modules:<AssemblyName> = <assembly-qualified name of the module type>
```

`ModuleBase.GetLoadedModules(IConfiguration)` reads it back: the host's own assembly first, then
each module after the modules it references, and by name otherwise. This is how
anything composed from modules — an EF Core model, a health check, a diagnostic endpoint — finds
out what is live, without the host having to know that modules exist.

**`AppDomain.CurrentDomain.GetAssemblies()` filtered by the attribute is not a substitute**, even
though it looks like one. While a single host owns the process it gives the same answer in the
same order — a referenced-but-never-activated module is not loaded there, because nothing uses its
types. It diverges in a process that runs more than one host, which is every integration-test
assembly: every topology's modules are loaded, so every topology sees the union. It also reports
hosting startups nobody in the application wrote — `Microsoft.AspNetCore.Server.IISIntegration` is
always present, and Application Insights, OpenTelemetry and `dotnet watch`'s browser refresh all
ship one. And design-time code has no host to inspect at all, so it needs an explicit list
regardless. The registry is one mechanism instead of two, and it never reports a module that did
not run.

`dotnet ef` does build the host, so `HostingStartup` runs and the registry is populated the usual
way — from `HOSTINGSTARTUPASSEMBLIES` as it stands in the shell that ran the command. That is the
hazard rather than the relief: unset, only the entry assembly activates and you get an **empty
migration with no error at all**; set to one topology, you get that topology's tables, also with
no error. `ModuleBase.CreateModuleRegistry(params string[])` builds the entries for a design-time
factory that names the modules in code, so the schema stops depending on whose shell produced
it — `UseModules(KnownModules.Names)` from `FusionModules.EntityFrameworkCore` does it for a
`DbContext`.

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

A module checks the modules it references from inside itself, so when nothing loads it is not
there to look. The host closes that with `builder.ValidateModules()`: generated from the host's
project references, it fails startup when a name in `HOSTINGSTARTUPASSEMBLIES` did not load or one
of the host's modules did not run — and the host's own code still names no module. A host that does
not call it is back to ASP.NET Core's critical log message and a healthy-looking process. MOD0003
covers the same class of mistake at build time.

### Project shapes

| | |
|---|---|
| Module, code only | `Microsoft.NET.Sdk` — ASP.NET types arrive through the package's framework reference |
| Module with `.cshtml` | `Microsoft.NET.Sdk.Razor`; FusionModules sets `AddRazorSupportForMvc`, needed for Razor Pages as much as MVC |
| Host | `Microsoft.NET.Sdk.Web` |
| Contracts | `Microsoft.NET.Sdk`, no `[HostingStartup]`, public types |

The host references its modules:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" />
```

An ordinary project reference, and nothing more: it orders the build and copies the assembly next
to the host so it can be loaded by name. The host still never uses its types.

**Do not set `ReferenceOutputAssembly="false"`.** It looks like hardening. The analyzer tolerates
it and the module then stops being copied to the output at all, so nothing loads and the failure
presents as a routing problem. MOD0004 is the enforcement mechanism, not MSBuild.

### MVC and Razor Pages

Application Part Discovery does not know about HostingStartup. Left alone, the Razor SDK emits
`[assembly: ApplicationPart("X")]` on the host for every MVC-flavoured project reference, and at
runtime `AddControllers`/`AddRazorPages` load those assemblies — so the host gets a module's
controllers **without ever running the module's code**, in every topology, including the ones
that excluded it.

FusionModules sets `GenerateMvcApplicationPartsAssemblyAttributes` to false for hosts. Each module
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

### One scoped context where there were several

Services that each had a context per request now share one per scope, and two habits break on it:

- **Calls that used to be separate HTTP requests** and are now method calls made in parallel from
  one page hit one context: "A second operation was started on this context instance". An
  in-process implementation of a contract creates a scope per call.
- **A service that disposes the context it was given** disposes everyone's.

More than twenty context types in one process trips EF Core's `ManyServiceProvidersCreatedWarning`;
it is a warning about options built per context, not about modules.

### Where entities go

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

### Taking a context apart: MOD0016

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

### One table, one owner: MOD0017

Every table, and every entity, belongs to the one module that names it with `ToTable` (or `[Table]`).
When the services move in, look for the places where two of them map one table: a reporting service
with its own copy of `Order` on `Sales.Orders`, two services each configuring a shared `Order`
class with a `ToTable` of its own. Separate processes let that pass; one host does not:

- **MOD0017** (error, in the host's build) reports a table or an entity that two modules the host
  references name with constants. Alternatives that no topology loads together are listed in
  `fusion_modules_allowed_table_collisions`.
- **The model refuses it at startup** (`ApplyModuleConfigurations`), with what the build cannot
  see: tables named by convention, names read from configuration, the model's default schema.

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

**The composing module's configuration is applied after the ones it extends** — not because of
where `HOSTINGSTARTUPASSEMBLIES` lists it: `ApplyModuleConfigurations` applies the modules in
`GetLoadedModules` order, which is the host's own module first (when the host is one), then each
module after the modules its assembly references, and a composing module uses their types. One set
of modules has one order in every topology and at design time, so it also has one EF Core model.

### Migrations

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

### Work that must finish before the port opens

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

With a schema per module on an existing database, never point a module at the old database before
its data has moved: the migrations history is looked up in the module's schema, is not found, and
the tables are created again beside the old ones. Under Aspire, `WaitFor` on a migration resource
waits for it to start; `WaitForCompletion` waits for it to finish.

If data has to move between physical databases, produce the plan — dump, transform, load,
verification queries — and, if downtime is not acceptable, the dual-write plus backfill plus
cutover sequence. Write it; do not run it.

### Gate

- Model snapshot per topology (`ctx.Model.ToDebugString()`) reviewed and stored as a golden file.
- After any change to `OnModelCreating` — MOD0016's fix or a hand-made one — the inventory compared
  with the one taken before: every host "the same".
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

Three lines of the host's own code, not package API. `ValidateModules()` already fails startup
when a *named* module cannot be loaded or did not run; this catches the other direction — a deployment whose variable
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
<PackageReference Include="FusionModules" Version="..." />
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
MOD0004 fires on day one and the noise hides real findings. A new project starts green.

```csharp
var builder = WebApplication.CreateBuilder(args).ValidateModules();

var app = builder.Build();

app.UseRouting();

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
```

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="..." />
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

### Kestrel endpoints

Merging services merges their listeners, and that cannot be a per-module decision: a module gets a
service collection and a place in the pipeline, never a port. Take the endpoint list out of Phase 0
and settle it here, once.

The trap is protocols. On a TLS endpoint `Http1AndHttp2` really is the union, because ALPN picks
per connection. On a **plaintext** endpoint there is no ALPN, so `Http1AndHttp2` means HTTP/1.1,
and every gRPC client that `Http2` would have served gets `HTTP_1_1_REQUIRED` on its first call.
The union reads like the safe merge of two services' settings, and it is the one setting that
silently drops gRPC.

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:8080", "Protocols": "Http1AndHttp2" },
      "Grpc": { "Url": "http://*:8081", "Protocols": "Http2" }
    }
  }
}
```

Keep the gRPC port on `Http2` unless something genuinely calls it over HTTP/1.1 — JSON transcoding
on the same port is the usual reason, and which services relied on that is worth knowing before you
take it away.

`Kestrel:EndpointDefaults:Protocols` set to `Http2` in a gRPC service's own `appsettings.json` does
not come along with the module. Without it gRPC works only over TLS, where ALPN negotiates, and an
HTTP launch profile or a CI job without certificates fails every call. A gRPC client also does not
resolve Aspire's `https+http://` scheme — give it one concrete scheme.

### An existing application as the host

Sometimes the answer to "which host" is the front end the services stood behind. Then the host is
not empty, and three things it already does now apply to every module:

- **Its default authentication scheme.** A storefront signs in with cookies; the APIs expected
  bearer tokens. Pin the host's defaults explicitly and give module endpoints a named policy
  ([modules.md](#phase-2-into-modules)).
- **An identity provider moved into it** takes the default schemes over on registration
  (`AddIdentity`). Pin `DefaultAuthenticateScheme` and `DefaultSignInScheme` back, tell the
  provider which cookie scheme is the application's, and give its pages a route prefix and their
  own cookie paths so they do not sit on the storefront's `/`. Discovery and token endpoints stay
  at the root: clients have their addresses. The issuer becomes the host's address — every client
  and every `ValidIssuers` list changes with it.
- **`ValidateOnBuild` in Development.** A front end that now consumes a module's contract cannot
  be built without that module, and Development validates the container at startup. A topology that
  leaves the module out fails there and passes in Production, which is where topology tests usually
  run. Decide what the front end does without the module and test that in both environments.

The order of `HOSTINGSTARTUPASSEMBLIES` is the order hosted services and startup work run in. Where
the deployment said "wait for the ordering service before starting the worker", that is now the
order of two names in one variable — write the reason next to it.

### Cross-cutting concerns, reconciled once

Phase 0 produced a diff table of middleware order and cross-cutting configuration across the
services. Reconcile it now, before any module is converted, and write the decisions down. Doing
it later means re-testing every module that has already moved.

The order of the host pipeline applies to every module. Where two services disagreed, one of them
is going to change behaviour — decide which, deliberately.

#### Once per process

Some calls configure one thing the process has one of. Each service made them for itself; the host
makes each **once**, and the widest variant any service used. Left in the modules they do not
fail at build, and most do not fail at startup:

| Called by every module | What happens |
|---|---|
| The message bus (`AddMassTransit`, a shared event bus) | Startup throws, or one configuration wins. One `SubscriptionClientName` is one queue for what were six. An EF outbox per context on one bus leaves one outbox registered. The host owns the bus and modules contribute consumers; where each needs its own outbox it is several buses, and a design decision. |
| `AddCarter()` and other "map everything found" calls | Every module maps every module's endpoints: several hundred where there were seventy, each route many times over. |
| A document store or cache under its default registration (`AddMarten`, a Redis cache) | The second module reads the first one's store. Register a store per module, by its own interface or key. |
| `AddAuthentication().AddJwtBearer(…)` | One scheme, one `Audience`. Listing every service's audience in `ValidAudiences` makes it start and gives up the isolation between them — a decision for the user, as is anything that builds policies from the audience. |
| `AddSwaggerGen` with the same document name | `An item with the same key has already been added. Key: v1`, on nearly every request. Two DTOs with one short name break `swagger.json` alone, while the UI page still answers 200. |
| Health checks without a `name:` | `Duplicate health checks were registered with the name(s): npgsql, npgsql` at the first `/health` — or only the first chain survives and `/health` says Healthy. Name each (`basket-npgsql`) and read the entries, not the status. |
| Service defaults | The host calls the widest variant; a module that relied on a wider one than the host chose fails to resolve `IHttpClientFactory`. And a wide call already contains what others call separately — a second `MapHealthChecks` makes `/alive` ambiguous. |
| A global MVC filter (`AuthorizeFilter`) | It does not come along unless someone moves it. Dropped, the controllers are open: the symptom was a 500 from the database where a 401 should have been. Put `[Authorize]` on the controllers. |

A duplicate **endpoint name** (`WithName("GetProductById")` copied between two services) is the
worst of this family: the host builds and starts, and then every request answers 500, `/health`
included — endpoint names are unique per process.

#### What stays out, and what nothing updates

Left out of the first merge on every real solution so far, and rightly: gateways (their terminal
middleware and catch-all routes belong to a process of their own), the front end unless it is the
host, and a service's separate gRPC or worker host built from the same infrastructure project as
its API.

And nothing rewrites the deployment for you: the Aspire AppHost still adds each service as a
project (it wants one resource with `HOSTINGSTARTUPASSEMBLIES`), the gateway's cluster addresses,
Dockerfiles, compose files, solution filters, and every `dotnet ef --startup-project` in a script.
A host started with `--no-launch-profile`, or by anything else that does not set the variable,
loads no module and answers 404 to everything. A relative path in configuration — a SQLite
`Data Source=discountdb` — now resolves against the host's folder and quietly creates a new, empty
database there. Named `Kestrel:Endpoints` replace `ASPNETCORE_URLS`: once one is named, name them
all.

From here on the host checks what its modules register, every time it starts: two modules
registering one service with different implementations fail startup, and two modules mapping one
route or configuring the same options differently are logged as warnings. Each start also logs one
line beginning `Topology:` — `modules: …; endpoints: …; hosted services: …; library hosted
services: …; shared on purpose: …`, each part left out when empty: the modules, the endpoints each
serves (and the host's own, as `host`), the modules that serve none, and the hosted services that
run — a worker removed after the modules registered it is not listed. Read it after every module you convert. What each check counts and how to answer it:
the [runtime reference](https://github.com/gandjustas/FusionModules/blob/HEAD/docs/runtime.md) and
[troubleshooting](#when-it-fails-quietly).

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
    <PackageReference Include="FusionModules" Version="..." />
  </ItemGroup>
</Project>
```

`Microsoft.NET.Sdk.Razor` instead, if the module ships `.cshtml`.

Set `<AssemblyName>` explicitly. It is the literal string an operator writes into
`HOSTINGSTARTUPASSEMBLIES`, so it must not follow the project file name — renaming a project
would otherwise break every deployment silently.

The SDK line is what bites first. `Microsoft.NET.Sdk.Web` contributes implicit usings that a plain
`Microsoft.NET.Sdk` project does not, so the first build of a converted service is a wall of CS0246
on types that are sitting right there in the framework reference. It reads like a missing package
reference and it is not one. FusionModules puts them back for a module project, so on a current
version there is nothing to do; on an older one, add them yourself:

```xml
<ItemGroup>
  <Using Include="Microsoft.AspNetCore.Builder" />
  <Using Include="Microsoft.AspNetCore.Http" />
  <Using Include="Microsoft.AspNetCore.Routing" />
  <Using Include="Microsoft.Extensions.Hosting" />
</ItemGroup>
```

The same line takes five analyzers away, and that one is silent in both directions — the build
stays green and the rules simply stop asking. They live in the Web SDK's own folder rather than in
the framework reference: the Startup analyzer (ASP0000, ASP0001) and MVC1000 through MVC1006, which
is the set an MVC or Razor Pages module actually needs. FusionModules restores those too. The ASP0xxx
rules about minimal APIs and routing come with the framework reference and never left, so do not
read a quiet build as proof that the rest is still on — check `@(Analyzer)` if you are on an older
version:

```bash
dotnet build path/to/Module.csproj -getItem:Analyzer
```

Then register it with the host and with the test project:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" />
```

In the test project as well as the host, so the module's assembly lands in the test output and
can be loaded there.

### Program.cs into Module.cs

```csharp
[assembly: HostingStartup(typeof(Module))]

namespace OrdersModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(IHostApplicationBuilder builder)
    {
        // everything that was builder.* in Program.cs, unchanged:
        // builder.Services.Add…, builder.AddNpgsqlDataSource(…), builder.Configuration[…]
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

### Let MOD0015 write the first draft

Do not do that move by hand for a whole service. MOD0015 (Info) marks a program whose own
registrations and endpoint mappings can become a module **in the same project**, and its fix writes
`Module.cs`. The project stays an executable and runs as before — ASP.NET Core runs the entry
assembly's own `[assembly: HostingStartup]` without it being named anywhere — so the service can be
converted, built, inventoried and committed before any host exists.

```bash
dotnet restore
dotnet format analyzers Shop.slnx --diagnostics MOD0015 --severity info --include src/Ordering.API/
```

- **`--severity info`**, or the rule is skipped: a suggestion is below the default `warn`.
- **The solution, not the project.** With the solution loaded, a project only this service
  references (its Application, its Infrastructure) counts as its own code and its registrations
  move; a library other programs share does not. Given the project alone, only its own source
  counts and less moves.
- **Restore first.** A project with a stale `obj` loads without the analyzer and is skipped without
  a word; `dotnet restore --force` when a project you expected is untouched.
- From Git Bash on Windows pass the path in Windows form. Given `/c/...`, `dotnet format` matches
  no document and changes nothing, without an error.
- It rewrites each converted project file (a redundant `<Compile Include="Module.cs" />`, a
  byte-order mark, blank lines). Nothing in it matters to the build; `git checkout -- '*.csproj'`
  after the fix keeps the diff to the code — commit the `PackageReference` first.

**What moves:** registrations whose every call is the project's own or names its own types;
mappings of its own endpoints, into `UseEndpoints`; the locals only they use; an `if` without `else`
on the environment or configuration around them; the own calls out of a chain on the service
collection.

**What stays in `Program.cs`, on purpose:** the framework's and shared libraries' registrations,
and everything that shapes the whole application even when its options name the service's types —
authentication, authorization, Identity, MVC, CORS, OpenAPI, the message bus, schedulers, service
defaults; middleware; migrations and seeding; `MapControllers`, `MapRazorComponents`, fallbacks,
health checks; anything order-sensitive (`Replace`, `Remove…`, a registration of someone else's
contract after a statement that stays); anything in a loop, an `if`/`else` or `#if`. That remainder
is the list to reconcile with the host in Phase 1 — read it as the fix's answer to "what of this
service belongs to the application", not as work it failed to do.

When endpoints move and the program is left with no routing of its own, the fix adds
`app.UseModuleRouting()` after `Build()`. Keep it rather than replacing it with `UseRouting()`:
`WebApplication` adds authentication, authorization and antiforgery by itself *before* an explicit
`UseRouting()`, where no endpoint is chosen yet, and every endpoint that requires authorization then
answers 500. `UseModuleRouting()` adds routing and then those three, as `WebApplication` would have.

Then read what moved, with one thing in mind: **a module's `ConfigureServices` runs before every
statement of `Program.cs`**. The fix holds back what it can see depends on order. What it cannot
see is a contract of the project's own that the framework or a shared library also registers, and a
`TryAdd` that used to lose and now wins. A hosted service that moved starts before the port opens.
Compare the inventory from before (`dotnet modules inventory … --compare before.json`,
[verify.md](#verification)) — it shows which registration wins and the order hosted services start in —
and run the service once.

When the service joins the host, the rest is a project-file change: `Microsoft.NET.Sdk.Web` becomes
`Microsoft.NET.Sdk`, `OutputType` goes, `Program.cs` is deleted after the host has taken what it
needs of it once, and the host references the project. Running the fix again is safe; it adds to
the module the project already has. Once done, `dotnet_diagnostic.MOD0015.severity = none` in the
project's `.editorconfig` silences a diagnostic that stays because a shared project's methods cannot
be read from a build.

**When it moves nothing.** A layered service whose `Program.cs` is one chain of the solution's own
methods — `AddApplicationServices().AddInfrastructureServices().AddApiServices()`, then
`app.UseApi()` — may have nothing the fix can take: the methods mix the service's registrations
with the application's. `dotnet format` then says the fix "did not return a Fix All action", once
per project. That is not an error. The work is to split those methods by hand — the service's own
registrations stay in the method and the method moves; authentication, the bus, OpenAPI go up to
the program — and it is real work: tens of files on a solution of nine such services. A program
that only calls `AddControllers` and `AddOpenApi` gets no diagnostic at all; write its module by
hand, empty if need be.

**The package reference is yours to add**, before the fix: `dotnet format` does not. Under central
package management that is a `PackageVersion` in `Directory.Packages.props` and a reference without
a version. If you script it, do not insert "after the last `<PackageReference`": one that spans
lines, with a `PrivateAssets` child, takes the new element inside itself (MSB4066), restore fails
for the whole solution, and unrelated projects report CS0246.

Things that keep their meaning only where they are written. The fix holds back the ones it
recognises; check them all the same, and apply the same list to anything moved by hand:

- **A registration that overrides another** by coming last: `AddTransient<IProfileService,
  ProfileService>()` after `AddIdentityServer().AddAspNetIdentity<…>()`. In the module it comes
  first and loses. Keep it after the call it overrides.
- **A registration that needs an earlier one**: a call that decorates what `AddIdentity`
  registered throws when it runs before it; a method that asks "is a health checker registered
  already?" quietly takes the other branch and four background services are never added.
- **A hosted service after the bus it uses.** Hosted services start in registration order. The
  order of log lines is not evidence — a bus logs from another thread; the inventory's start order
  is.
- **`Assembly.GetExecutingAssembly()`** in a statement that changes assembly: a consumer scan
  handed the wrong assembly builds, starts, and consumes nothing. Write
  `typeof(SomethingThere).Assembly`.
- **A local** every statement that uses it must stay with (CS0103 otherwise), and one that moved
  without its users is dead code.
- **An early `return` or `else`** in a method being split: `if (IsBuild()) { …; return; }` at the
  top guarded everything below it, and statements lifted out now run unconditionally.
- **A type named `Module` or `ModuleBase`** in the service's namespace (CS0115).

What usually has to be finished by hand, and is worth doing right after the fix rather than at the
merge:

- **A chain that starts with a shared library's call** stays whole even though the rest names the
  service's types: `builder.AddRabbitMqEventBus("eventbus").AddSubscription<OrderPaid, OrderPaidHandler>()`.
  Leave the first call in the program and write the subscriptions in the module.
- **An `if … else`** around registrations (one AI provider or the other) blocks them. Decide by
  hand; the condition usually reads configuration and moves as it is.
- **A `private` helper** a moved statement calls has to become `internal`.
- **Uniformity.** After the pass every service should look alike: a thin `Program.cs` of
  application-wide calls and a `Module.cs`. One that kept its wiring in `Program.cs` was skipped or
  held back by something — find out which before going on.

Do not copy what stayed into each module to make the module "complete". `AddDefaultAuthentication`,
`AddProblemDetails`, API versioning, OpenAPI and service defaults belong to the program now and to
the host later, once. A module that needs a marker to make such a call idempotent is telling you
the call is in the wrong place.

It is not reported for a worker on `Host.CreateApplicationBuilder` (a hosting startup runs only
under the web host), for a project on .NET 7 or older, or in a host that already composes modules.

### The builder is the module's view of the host

`ConfigureServices` takes an `IHostApplicationBuilder`, the same type `Program.cs` had, so the
service's registrations move over unchanged. That matters most for what is written as an extension of
the builder rather than of `IServiceCollection` — every Aspire client integration, and most component
packages. Those are not decoration over `AddDbContext`: they resolve `ConnectionStrings:<name>` out of
configuration, register a health check and instrument the calls. Registering the client by hand keeps
none of it.

```csharp
protected override void ConfigureServices(IHostApplicationBuilder builder)
{
    builder.AddNpgsqlDataSource("orders");
    builder.AddRedisClient("cache");
    builder.Services.AddScoped<IOrders, Orders>();
}
```

It is the module's view of the host rather than the host: the configuration is readable and closed
to new sources — `ConfigureAppConfiguration` is where those go, and adding one here throws rather
than disappearing — and the container is not a module's choice. `builder.Environment` is the web
host's environment; cast it to `IWebHostEnvironment` for `WebRootPath`.

Before 0.2 a module had a second overload, `ConfigureServices(WebHostBuilderContext, IServiceCollection)`,
the shape of the classic `Startup`. It still runs, after this one, and overriding it is a CS0672
warning whose code fix moves the body here (`services` becomes `builder.Services`, `context.Configuration`
becomes `builder.Configuration`, `context.HostingEnvironment` becomes `builder.Environment`). Splitting
registrations across both — the database in one, everything else in the other — was what most
migrated modules ended up doing, for no reason but that there were two.

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

### What a service assumed about being the process

None of these is a collision between two modules, so neither the startup checks nor
`dotnet modules topology` report them. Each was true while the service was the entry point and
stops being true, silently, in a host. Grep for them when a service is converted:

- **`Assembly.GetEntryAssembly()`, `typeof(Program)`.** The entry assembly is the host's now. A
  type lookup by name over it returns null — an event log that deserializes by event type name
  throws on the first event — and an assembly scan finds the host's handlers instead of the
  module's. Name a type of the module (`typeof(Module).Assembly`).
- **`ContentRootPath`, `WebRootPath`.** The host's. Files the service read from its own folder
  (seed data, pictures) are copied to the output and read from the module assembly's directory.
- **`appsettings*.json` and `launchSettings.json` of the service.** Dead once it is a library.
  Merge the keys into the host's, which is where the configuration-key collisions of Phase 0 get
  their answers.
- **Middleware the service added for itself.** In a module's `Configure` it runs for every request
  of the process, after the host's routing: `UseStatusCodePages()` of an API starts rewriting the
  storefront's pages. Confine it — `app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), …)`.
  Or it does not run at all: when the host maps an endpoint of its own, `WebApplication`'s endpoint
  middleware sits in the host's pipeline and executes the module's endpoints before the module's
  middleware is reached. Send a request and look for the middleware's effect; where it must wrap
  the module's endpoints and nothing else, an endpoint filter on the module's group says so
  directly.
  And middleware that had to run *before* routing no longer does: a cookie policy that set
  `SameSite` for the sign-in cookie stops covering it, and login loops over plain HTTP. Say it in
  the options of the thing itself instead.
- **`RequireAuthorization()` with no arguments.** It means the host's default scheme. An API that
  expected a bearer token, inside a host whose default is a cookie, answers a mobile client with a
  redirect to the login page instead of 401. Give the module's endpoints a named policy that names
  its scheme, gRPC services included.
- **A dictionary keyed by a short type name.** An event bus that maps `nameof(TEvent)` to one
  handler type worked while each service had its own copy of `OrderPaidIntegrationEvent`. With four
  assemblies declaring it, the last subscription wins and the other three handlers never run.
- **`wwwroot`.** Two projects with a `wwwroot/css/site.css` fail the host's build as soon as it
  references both. Set `StaticWebAssetBasePath` (`_content/<AssemblyName>`) in the module and fix
  the links in its views.
- **Build-time tooling that needs an entry point** — OpenAPI document generation at build, for
  one. It goes, or moves to the host.

### When the project becomes a library

MOD0001, MOD0004 and MOD0005 judge a project by what it is: while a converted service is still an
executable it is a host, and they are silent. They arrive together at the step that drops
`OutputType` — a couple of hundred MOD0001 on a mid-sized solution is normal. Do not add a switch to
silence them in between; there is nothing to silence until then. Do it as a step of its own:

```bash
dotnet format analyzers src/Ordering.API/Ordering.API.csproj --diagnostics MOD0001 --severity info --no-restore
```

Then the second wave, which no rule reports and the first build after the fix does not always
show:

- **Anything that scans the assembly for public types.** FluentValidation's
  `AddValidatorsFromAssemblyContaining<T>()` skips internal validators unless told
  `includeInternalTypes: true` — requests then pass unvalidated and no unit test fails.
  MassTransit's `AddConsumers(assembly)` takes public consumers only, and the message is then
  simply never handled. The framework's own `AddValidation()` skips internal types; Carter modules
  and a document database's document types have to stay public. Check every scan — validators,
  handlers, mapping profiles, consumers — and keep what must stay public with
  `fusion_modules_allowed_public_types`, each with its reason.
- **Generated code.** Source-generated partials (a `JsonSerializerContext`) stay as the generator
  wants them; `Protobuf` items take `Access="Internal"`.
- **Tests.** `InternalsVisibleTo` for the test project, and for `DynamicProxyGenAssembly2` when it
  mocks internal types (NSubstitute, Moq). It does not let a public test class name an internal
  type in a public signature (CS0050, CS0051): a host that builds can sit beside dozens of errors
  in the test projects.
- **Code generated from a shared file.** A `.proto` compiled into the module and again into the
  host as a client gives two copies of the same namespace in one compilation.
- **Six `Program` classes.** Reference the module projects from the host — and from tests — with
  an alias until their programs are deleted; see [verify.md](#verification).

### Visibility

Build. MOD0001 will report every public type. Apply the "make internal" fix — Fix All handles a
converted service in one pass.

Exempt by default, because the framework finds them by reflection: view components, tag helpers,
the client interface of a SignalR `Hub<TClient>`, and entity types configured by an
`IEntityTypeConfiguration<T>` in the same assembly.

Controllers, page models and hubs are not exempt. A public class cannot have a public constructor taking an internal
service, nor a public action returning an internal DTO — so a public controller drags its services
and its models public with it, and then whatever those name. MOD0001 reports it and its fix makes
it internal; let discovery see it, or MOD0011 reports every route answering 404 (its fix writes
this):

```csharp
services.AddControllers()
    .AddApplicationPart(typeof(Module).Assembly)
    .AllowInternalControllers();
```

The class becomes `internal`, the constructor stays public — MVC's activator reads
`GetConstructors()`, which is public-only — and from there the parameters and return types may be
internal, because C# bounds a member's effective accessibility by its containing type. One call
covers the whole application.

The other kinds the framework reaches each answer differently, and guessing costs
a day:

- **View components** — `.AllowInternalViewComponents()`, same call site. Invoke by name or through
  the generic overload; both resolve at run time and both work. The one form that does not is
  `<vc:…>`, which the Razor compiler binds and which needs a public type — finding none, it reports
  nothing and copies the element into the page as literal HTML.
- **Razor Pages** — MOD0001 reports a public `PageModel` like a controller, and its fix makes it
  internal; nothing else to call. Page discovery reads the attributes the Razor compiler emits
  rather than scanning types, and the generated page class is itself internal. Keep the
  constructor and handler methods public.
- **SignalR hubs** — MOD0001 reports a public hub too, and its fix makes it internal; nothing else to
  call, because `MapHub` names the type. Keep the constructor and hub methods public. The client
  interface of a `Hub<TClient>` is the exception and stays public, for SignalR's generated proxy
  (MOD0010).
- **Tag helpers** — keep them public. `@addTagHelper` binds in the compiler against Roslyn symbols
  and requires public, for the current assembly as much as for referenced ones. An internal tag
  helper produces no descriptor at all: the element renders as literal HTML, with no error, no
  warning and nothing at runtime. Add it to the list of things the route inventory cannot see.

A hub's client interface needs a second sentence for the opposite reason: MOD0001 leaves it alone,
but SignalR still has to reach it. The proxy for a `Hub<TClient>` is generated into a dynamic
assembly of its own, which cannot implement an `internal` interface declared in yours — and nothing
says so until a client connects and the process throws `TypeLoadException`. MOD0010 asks the
question at build time; the usual answer is one line:

```csharp
[assembly: InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]
```

What remains is the interesting part: a public type with no reason to be public is a leak, and a
public type with a real reason is a **contract**. Move contracts into a plain `Microsoft.NET.Sdk`
library with no `[HostingStartup]` — not a module, so MOD0001 does not apply and its types are
meant to be public. That library is also the answer whenever two modules need to share a type
without one depending on the other's deployment.

For the genuine exceptions the analyzer cannot infer — a DTO bound by a source-generated
`JsonSerializerContext` elsewhere, a gRPC service base, a message contract a broker discovers —
use `.editorconfig`:

```ini
fusion_modules_allowed_public_types = OrderDto, PaymentEnvelope
```

Write down why. Nobody can tell a considered exception from an abandoned one.

### Views and static assets

A module ships its own `Areas/`, `Pages/` and `wwwroot/`. It adds itself as an application part
inside `ConfigureServices`; the host must not, and FusionModules turns off the SDK behaviour that
would have done it silently. Static assets are served from `_content/<AssemblyName>/`, so two
modules can both ship `site.css`.

Which is where an asymmetry lives, and it is worth stating out loud: controllers and pages are
gated by `HOSTINGSTARTUPASSEMBLIES` and static assets are not. The static web asset manifest is
built from project references at build time, so `_content/StatusModule/status.css` is served by a
topology that never named `StatusModule`, while that same module's controllers correctly stay away.
It turns up in a route inventory diff as something the pre-migration service did not serve, and the
usual right answer is to leave it — a CSS file reachable by URL is not an endpoint anyone can act
on. When it is more than that, a demo page or an internal tool, confine `MapStaticAssets()` to
Development in the host. Excluding the asset properly means not referencing the project, which
means a second host, and that is rarely worth it.

### Per-module gate

Before moving to the next module:

1. `dotnet build -warnaserror` — no MOD diagnostics.
2. The route inventory for the topology containing this module matches what the service served
   before. See [verify.md](#verification).
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
   protected override void ConfigureServices(IHostApplicationBuilder builder) =>
       builder.Services.AddScoped<IPricing, Pricing>();
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
`fusion-modules-migration.md`.** Never collapse a queue because it is technically possible.

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
- The `fusion-modules-migration.md` entry for each edge says what was decided and why.

---

## When it fails quietly

Almost every failure mode of this approach is silent. The application starts, the probe passes,
and something is simply missing. Work from the symptom.

### An endpoint returns 404 in a topology that should have it

Read the `Topology:` line in the startup log first: its `endpoints:` part counts what each loaded
module serves, and the host's own as `host` (even when the host is a module itself), and names the
modules that serve nothing —
`(none from Local, Orders.Entities)`. A module missing from `modules:` was not loaded. One under
`none from` was loaded and mapped nothing: expected of a transport or a module of entities,
suspicious only for a module meant to serve requests. Endpoints whose handler is the framework's or a
library's (a health check, `MapOpenApi`) are not counted for anyone.

In order of likelihood:

1. **The module's assembly is not in `HOSTINGSTARTUPASSEMBLIES`.** Check the actual environment
   variable in the running container, not the compose file you think it came from.
2. **The module has no `[assembly: HostingStartup(typeof(...))]`.** ASP.NET Core loads the
   assembly, finds no attribute, and moves on without a word. MOD0003 catches this at build time —
   if it did not fire, the build was not run with the analyzers.
3. **The module's `Configure` maps into a group or area whose prefix you have forgotten.**
4. **Two modules mapped the same template** and load order decided it — or both match, and the
   request gets a 500 instead (below).

Routing is not on this list. A host with no routing does not serve 404s — startup throws, naming
`UseRouting` in the message. See below.

Confirm what actually loaded rather than reasoning about it:

```csharp
ModuleBase.GetLoadedModules(configuration).Select(a => a.GetName().Name)
```

### Startup fails: "Two modules register the same service with different implementations"

Before the merge every service had a container. After it there is one, and for a service resolved
as a single instance the last registration wins — one module quietly gets the other's
implementation. Startup refuses instead, naming the service, both modules and both implementations.
The fix is **not** `TryAdd`: that turns last-wins into first-wins, and one module still gets the
wrong implementation. Instead:

- if the two modules are **alternatives** — two transports, two implementations of one capability
  that each deployment chooses between — no topology should load them together: remove one from
  `HOSTINGSTARTUPASSEMBLIES`. This comes first in the message, and it is the fix whenever it applies;
  keying the registration would only hide the mistake;
- otherwise register **keyed**, one key per module; or
- register a **type of each module's own**: a concrete type, or a module-internal interface;
- if the type is meant to be consumed as `IEnumerable<T>` (seeders, handlers, listeners), put an
  attribute named `MultipleImplementationsAttribute` on it — declared in the contracts library
  itself, internal, matched by name — or list a type you do not own under
  `FusionModules:AllowedServiceCollisions`.

There is no setting that turns the check off. What is and is not a collision is in the
[runtime reference](https://github.com/gandjustas/FusionModules/blob/HEAD/docs/runtime.md).

MOD0014 asks the same question in the host's build, for every pair of modules the host references,
from the registrations written in the modules' own code. Modules that are alternatives — never
loaded together — go in `fusion_modules_allowed_service_collisions` (`.editorconfig` or an MSBuild
property); that list silences the build warning only. `ValidateModules()` hands it to the startup
check as `FusionModules:AlternativeServices`, so when two of those modules load together after all,
startup still fails and the message says plainly to load only one of them.

### The model is refused: "maps a table or an entity from two modules"

`ApplyModuleConfigurations` refuses a model in which two modules own one table or one entity, and
names the table or the entity and the modules: both name the entity's table, or each maps an entity
of its own onto one table — `by convention` when no one called `ToTable` and the class names
matched. It fires when EF Core builds the model, which in a host that migrates at startup is startup,
and in `dotnet ef` and in topology tests too. Pick the owner and follow "One table, one owner" in
[data.md](#phase-3-data). If the two modules are alternatives, remove one from
`HOSTINGSTARTUPASSEMBLIES`. `FusionModules:RuntimeTopologyChecks=false` turns it off with the other
startup checks; it is not a fix.

MOD0017 asks the same question in the host's build, of the constant names in the modules' code.

### A warning at startup: "Routes mapped by more than one module"

Two modules map one route — same template up to parameter names, overlapping HTTP methods — and the
first request to it answers 500 with `AmbiguousMatchException`. The warning names each route and the
modules. Give each module a `MapGroup` prefix or an MVC area. It is a warning rather than a failure
because a policy or an endpoint filter can tell such endpoints apart, which the check does not model.

A warning "Endpoints could not be read at startup" next to it means a handler could not be built,
usually for a service the topology lacks; the first request to it would fail the same way.

Its neighbour, "Options configured differently by more than one module", names an options type
(`JsonOptions`, `HubOptions`, …) that several modules configure: there is one per process, so each
module's settings reach the others. Move what must differ to a per-hub, per-endpoint or named
instance.

### `TypeLoadException` the first time a client connects to a hub

```
Type 'Microsoft.AspNetCore.SignalR.TypedClientBuilder.IPaymentClientImpl' ...
is attempting to implement an inaccessible interface.
```

The client interface of a `Hub<TClient>` is internal. SignalR generates the proxy into a dynamic
assembly of its own, which cannot implement it. One line in the module fixes it and keeps the
module's surface intact:

```csharp
[assembly: InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]
```

MOD0010 catches this at build time. It is worth knowing what survives it otherwise: a clean build,
a green MOD0001, a full test run, and an unchanged route inventory — nothing but a live hub resolve
triggers it.

### Every gRPC call fails with `HTTP_1_1_REQUIRED` after the merge

The merged host's gRPC endpoint is plaintext and set to `Http1AndHttp2`. Without TLS there is no
ALPN, so the connection is HTTP/1.1 and gRPC refuses it. Set that endpoint's `Protocols` to
`Http2` — see [the host](#phase-1-the-host). The union of two services' settings is not a superset here.

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

### A migration comes out empty, or covers the wrong modules

`dotnet ef` builds the host, so `HostingStartup` runs and the model follows
`HOSTINGSTARTUPASSEMBLIES` as set in the shell that ran the command: unset gives an empty model,
one topology's value gives that topology's tables. There is no error either way — `Up` is just
empty, or short. Generate migrations through a design-time factory that names every module with
`UseModules(KnownModules.Names)` and ignores the environment. See [data.md](#phase-3-data).

### A topology has tables it should not, or is missing tables it should have

EF Core's model cache, keyed by context type, shared across hosts in one process. The second
topology in a test run gets the first one's model. Nothing throws. `UseModules` from
`FusionModules.EntityFrameworkCore` puts a key that includes the module set in place; a context that
replaces `IModelCacheKeyFactory` itself after it loses that key and has to include
`context.GetModuleSetKey()` in its own — [data.md](#phase-3-data).

If it happens at runtime rather than in tests, look at what the model is composed from. An
`AppDomain.CurrentDomain.GetAssemblies()` scan reports the application's own modules correctly
while one host owns the process, but it also reports hosting startups that arrived with a
package.

### Controllers or pages appear in a topology that excluded their module

Application Part Discovery. The host has
`GenerateMvcApplicationPartsAssemblyAttributes` unset or true, so the SDK wired the module's
controllers in at build time, behind `HOSTINGSTARTUPASSEMBLIES`' back. MOD0007 reports it; the
fix is the property plus each module calling `AddApplicationPart` for itself.

### Startup fails: "requires X, which was not activated"

Correct behaviour. The module uses types from another module, so it depends on it. Either add
that module to the topology, or — if the dependency is only a shared DTO — move the type into a
contracts library so the reference goes away.

Do not silence this by removing the project reference: the module would then fail to load at all.

### Startup fails: "'X' could not be loaded"

`ValidateModules()` found a name in `HOSTINGSTARTUPASSEMBLIES` that is not loaded in the process:
a typo, or the assembly is not deployed next to the host. Check for
`ReferenceOutputAssembly="false"` on the project reference — it stops the module being copied to
the output, which looks like a routing problem until you look in the folder.

### Startup fails: "'X' is a module, but its startup did not run"

The assembly loaded and is one of the host's modules, but ASP.NET Core never ran it: look for
`HOSTINGSTARTUPEXCLUDEASSEMBLIES`, or `ASPNETCORE_PREVENTHOSTINGSTARTUP` set to `true`.

### Nothing at all happens and no module loads

The host does not call `ValidateModules()`, so nothing compares the variable with its modules —
MOD0012 says so at build time, and its code fix adds the call. Add it right after the builder is
created; until then, look at the log for ASP.NET Core's own
critical message about the first name it could not load.

### Every request answers 500 after a module was added

Something the process has one of was given twice. `Duplicate endpoint name '…'`: two modules use
one `WithName`. `An item with the same key has already been added. Key: v1`: two `SwaggerDoc`s.
`AmbiguousMatchException` on one path: two modules, or a module and a shared call the host also
makes, map it. See "Once per process" in [the host](#phase-1-the-host).

### Every request answers 404, the host's own included or not

No module loaded: the host was started without the launch profile or the variable that names them.
The `Topology:` line in the log has no `modules:` part.

### `/health` throws, or says Healthy too easily

`Duplicate health checks were registered with the name(s)` — checks registered without a `name:` by
several modules. If it answers Healthy, count the entries: only one module's chain may have
survived.

### A message is published and nothing handles it

In order: the consumer became internal and the scan takes public types; the scan was handed
`Assembly.GetExecutingAssembly()` and now runs in another assembly; the bus is configured once and
this module's consumers were never contributed to it; or two modules share one queue name and the
other one took the message.

### An API endpoint redirects to a login page instead of answering 401

The module's endpoints say `RequireAuthorization()` and the host's default scheme is a cookie. Give
them a named policy that names the bearer scheme — [modules.md](#phase-2-into-modules).

### Login loops after the identity service became a module

In order: the identity registration took over the host's default schemes; the provider does not
know which cookie scheme is the application's; or a cookie policy that ran before routing in the
service runs after it in the module, and over plain HTTP the sign-in cookie is dropped for its
`SameSite`. See [the host](#phase-1-the-host).

### A handler stopped running, or a type cannot be found by name, only in the merged host

`Assembly.GetEntryAssembly()` or `typeof(Program)` in the module or in a library it uses: the entry
assembly is the host's. Or a registry keyed by a short type name that several modules now share.
See "What a service assumed about being the process" in [modules.md](#phase-2-into-modules).

### Validation stopped happening after types were made internal

An assembly scan that takes public types only — FluentValidation without
`includeInternalTypes: true`. Nothing fails; requests pass unvalidated.

### The build cannot write a module's DLL (MSB3021, MSB3027)

Something still runs it: an Aspire AppHost left up, or a build server holding the file. Stop the
AppHost, `dotnet build-server shutdown`, and give verification builds their own output directory.

### A package was rebuilt under the same version and nothing changed

NuGet restored the copy in its global packages folder. Delete that version's folders there —
`dotnet nuget locals global-packages --list` says where it is, and `NUGET_PACKAGES` may have moved
it — then `dotnet restore <solution> --force` and `dotnet tool restore`.

### Behaviour changed after merging, in a way nobody can pin down

The usual suspects, in order:

1. **Middleware order.** Each service had its own; now there is one. Phase 0's diff table says
   which ones differed.
2. **A configuration key defined by two modules** with different values. Last write wins.
3. **One service registered with `TryAdd` in two modules.** Startup fails when two modules both
   add a service, but a `TryAdd` that added nothing leaves no trace: the module activated first
   wins, and the other is given its implementation.
4. **A hosted service now running in more replicas than before**, or more than once per topology.
5. **A retry or circuit breaker around what is now a local call**, changing failure behaviour.

### The diagnostics

| | |
|---|---|
| MOD0001 | A module must not expose public types. Make it internal, or move it to a contracts library, or allow it in `.editorconfig` with a reason. |
| MOD0002 | The type in `[assembly: HostingStartup]` must derive from `ModuleBase`. The fix converts a hand-written `IHostingStartup`. |
| MOD0003 | A module nothing names in `[assembly: HostingStartup]`. It would load and do nothing. |
| MOD0004 | The host uses a module's types. Move the type to a contracts library. Keep the project reference — the model needs it. |
| MOD0005 | A plain library uses a module's types, and takes the module wherever it goes. Move the type to a contracts library. |
| MOD0006 | The host is built with `CreateSlimBuilder` or `CreateEmptyBuilder`, which load no module. The fix switches to `CreateBuilder`. |
| MOD0007 | The host declares an `[ApplicationPart]` for a module. Set `GenerateMvcApplicationPartsAssemblyAttributes` to false. |
| MOD0008 | A module calls `IWebHostBuilder.Configure` or `UseStartup`, which replace the pipeline rather than add to it. Override `ModuleBase.Configure`. |
| MOD0009 | `ModuleBase` already registers the module as an `IStartupFilter`. Registering it again runs `Configure` twice. The fix removes the registration. |
| MOD0010 | A hub's client interface is not visible outside the module, so SignalR's generated proxy cannot implement it. Grant the proxy's assembly access to internals, or make the interface public. |
| MOD0011 | MVC never finds the module's controllers, pages or views: no `AddApplicationPart` for its assembly, or internal controllers without `AllowInternalControllers()`. The fix writes the call. |
| MOD0012 | A host that references modules never calls `ValidateModules()`, so a typo in `HOSTINGSTARTUPASSEMBLIES` goes unnoticed. The fix adds the call. |
| MOD0013 | The FusionModules package is not referenced, so the rules that need `ModuleBase` are inactive and the build is green because nothing is being checked. |
| MOD0014 | Warning, in the host: two modules it references register one service with different implementations, so where they load together one gets the other's. Register keyed, or a type of each module's own; `[MultipleImplementations]` on a contract read as `IEnumerable<T>`; list alternatives no topology loads together in `fusion_modules_allowed_service_collisions`. The startup check catches the same thing at runtime, including registrations made inside libraries. |
| MOD0015 | Info, not a defect: this program's own registrations and mappings can move into a module in the same project. The fix writes `Module.cs`; run it with `dotnet format analyzers <solution> --diagnostics MOD0015 --severity info`. See [modules.md](#phase-2-into-modules). |
| MOD0016 | Info, not a defect: entity configuration in `OnModelCreating` can become `IEntityTypeConfiguration<T>` classes and `DbSet` properties extension members of `DbContext`. Same command with `MOD0016`; compare inventories before and after. See [data.md](#phase-3-data). |
| MOD0017 | Error, in the host: two modules it references map one table, or give one entity a table, with constant names (`ToTable`, `[Table]`). Pick the owner: read through its contract, move the entity to it, or read through a view. List alternatives no topology loads together in `fusion_modules_allowed_table_collisions`. The model's check at startup sees tables named by convention and from configuration too. See "One table, one owner" in [data.md](#phase-3-data). |

MOD0015 and MOD0016 are suggestions, so `dotnet build -warnaserror` does not fail on them and
"zero MOD diagnostics" does not mean them. If `dotnet format` changed nothing: `--severity info` is
missing, the path was given in `/c/...` form from Git Bash, or the project's `obj` is stale and it
loaded without the analyzer (`dotnet restore --force`). If the build then fails with NETSDK1022, the
project consumes the analyzers without the package's build targets: delete the `<Compile Include>`
lines `dotnet format` added.

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

#### When every service declares `Program` in the global namespace

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

### 2. What the inventory cannot see

It is the best signal you have and it is not a complete one. Three classes of failure leave the
inventory byte-identical and the process broken.

**Anything built by reflection at resolve time.** The client proxy of a SignalR `Hub<TClient>`, a
`JsonSerializerContext` in another assembly, `ActivatorUtilities` over a type named in
configuration, a gRPC service base. The endpoint is in the table; the first request into it throws.

**Anything negotiated on the wire.** Protocol, TLS, ALPN, authentication scheme. The table says a
gRPC method is mapped — whether a gRPC client can reach it is a property of the Kestrel endpoint
the call arrives on, and that is in no route. See [the host](#phase-1-the-host).

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

### 3. Build

```bash
dotnet build -warnaserror
```

Zero MOD diagnostics. If MOD0004 fires alongside compiler errors, fix the compiler errors first —
Roslyn cannot work out which references are used in a broken compilation, and the analyzer
suppresses itself in that case, so anything you do see is real.

### 4. Boot every topology

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

### 5. Two kinds of test, kept apart

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

### 6. Model snapshots

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
([data.md](#phase-3-data)): nothing reported means the model is the same. A filter that moved to another
class is the same filter. `model of … not compared` is a note, not a pass: one of the two
inventories has no model for that context — it was taken by an older version of the tool, or the
context could not be created outside a request (a tenant, say) and `modelError` says why. With
`--merged`, a context type several services had must have in the host every entity type any of them
had, each as it was.

### 7. Collisions

- No configuration key defined twice with different values across modules.
- No non-`TryAdd` registration of the same service type in two modules.
- No duplicate route templates.

All three are silent at runtime; a test is the only place they will be noticed.

### 8. Contracts, for transports that were kept

The remote and local implementations of an interface pass the same suite. The pattern gives you
this for free; it costs one shared test class to collect.

### 9. Before and after, under load

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

### 10. Containers

Every topology in the compose file starts and answers its health probe. `docker compose up` is
part of verification, not a separate activity.
