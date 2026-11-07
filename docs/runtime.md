# At runtime: checks, settings and the startup report

`ModuleBase` does a few things when the host starts, besides running each module's own
`ConfigureServices` and `Configure`. Each of them turns a failure that would otherwise be silent —
the application starts, the probe passes, something is quietly wrong — into a startup failure or a
log line. This page lists them, the configuration keys that steer them, and what to set when the
default is not what you want.

All keys are ordinary configuration: `appsettings.json`, environment variables
(`FusionModules__AllowedServiceCollisions`), command-line arguments. A list can be written as JSON
array children or as one value separated by `;` or `,`. A comma inside angle brackets belongs to
the type — `IHandler<A, B>` is one entry — and spaces inside a name do not matter.

| Key | Default | What it does |
|---|---|---|
| `FusionModules:AllowedServiceCollisions` | empty | Service types several modules may register with different implementations |
| `FusionModules:AlternativeServices` | written by `ValidateModules()` | The host build's `fusion_modules_allowed_service_collisions`, separated by `;`: contracts its modules implement as alternatives. Only words the collision message — see [below](#alternatives-the-hosts-build-already-knows) |

## When the checks run

When the request pipeline is built — during `app.StartAsync()` / `app.Run()`, not during
`builder.Build()`. A host that builds and never starts (a migration host that applies the schema
and exits, say) runs none of them; validate those topologies in a test that starts the host.

They run once per host however many modules are loaded, and the first failure stops startup with
an `InvalidOperationException` that names what to change.

Two checks of the module list come before these, and are not configured here either. The generated
`builder.ValidateModules()` fails startup when a name in `HOSTINGSTARTUPASSEMBLIES` did not load or
did not run (see [MOD0012](rules/MOD0012.md)). And a module that uses another module's types needs that
module activated: the compiler emits a reference only for an assembly whose types are used, so a
module's references are exactly the modules it depends on, and a missing one fails startup naming both.

## Turning the runtime checks off

```csharp
var builder = WebApplication.CreateBuilder(args).ValidateModules(runtimeTopologyChecks: false);
```

or, without touching the code, `FusionModules:RuntimeTopologyChecks=false` in configuration
(`FusionModules__RuntimeTopologyChecks=false` in the environment). The check of
`HOSTINGSTARTUPASSEMBLIES` that `ValidateModules()` itself makes still runs; what stops is everything
below that the modules do when the pipeline is built: service and options collisions, route
collisions, and the endpoint counts in the startup report, which says "not read: runtime topology
checks are off" instead. The endpoints are then built on the first request, as ASP.NET Core does
without this package.

The checks cost a pass over the registrations before the pipeline is built and building the
endpoints at startup rather than on the first request; nothing per request. Switch them off for a
host whose topologies are covered by MOD0014 and by tests that start each of them — for start-up
time in a scale-out, say — not to get past a collision.

## Two modules registering one service

Before the merge each service had a container of its own; after it there is one, and for a
service resolved once the last registration wins without anyone being told. Startup fails when two
modules register the same service type with different implementations, and the message names the
type, both modules and both implementations. In eShop the Catalog and Ordering outboxes each
registered an event log service, and one module wrote its events through the other's `DbContext`.

First ask whether the two modules belong together at all. Two transports, two implementations of one
capability, are alternatives: each deployment picks one, and the fix is the module list — remove one
of them from `HOSTINGSTARTUPASSEMBLIES` — not the registrations. dotnext-2026's Local and RemoteGrpc
transports are such a pair. Otherwise, register keyed (one key per module), or register a type of each
module's own. `TryAdd` is not the fix: it turns last-wins into first-wins, and one module still gets
the wrong implementation. The message gives the advice in that order:

```text
If these modules are alternatives that no topology should load together, remove one of them from HOSTINGSTARTUPASSEMBLIES.
Otherwise register a keyed service, or a service type of each module's own. TryAdd does not help: ...
If the type is meant to be consumed as IEnumerable<T>, put an attribute named MultipleImplementationsAttribute
on it (declared anywhere; matched by name), or list it under FusionModules:AllowedServiceCollisions when the type is not yours.
```

The check runs at startup because that is where it can see everything: registrations made inside
library extension methods, which no analyzer sees, and the set of modules this topology actually
puts together. It judges the registrations the container still has when the pipeline is built: one
the host or a test removed after the modules — `ConfigureTestServices` replacing a module's service —
no longer collides.

### Alternatives the host's build already knows

When the host lists the contract in `fusion_modules_allowed_service_collisions` (see
[MOD0014](rules/MOD0014.md#modules-that-are-never-loaded-together)), its build already says the
modules are alternatives, one per topology, and the message need not ask. The generated
`builder.ValidateModules()` writes that list, as the build read it, under
`FusionModules:AlternativeServices` — only when it is not empty — and for a contract on it the
message says so instead of the general advice:

```text
Pricing.Contracts.IPricing is listed in the host's build as implemented by alternative modules
(fusion_modules_allowed_service_collisions): load only one of PricingClientModule, PricingModule in a
topology — remove the others from HOSTINGSTARTUPASSEMBLIES.
```

The modules are named in order of their names, not of their registrations, so the sentence reads the
same whatever order `HOSTINGSTARTUPASSEMBLIES` names them in.

The list is not an allow-list here. Two alternatives loaded together still hand one module the
other's implementation, so the collision still fails startup; the key only chooses the words.
Contracts not on it keep the general advice above.

Not a collision:

- the same implementation registered by two modules;
- keyed registrations with different keys;
- the host's own registrations — the host may override a module's default deliberately;
- the framework's own contracts (`System.*`, `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`
  and the like), which it registers through `TryAddEnumerable` to be read as sequences, and EF
  Core's non-generic `DbContextOptions`, which two contexts in one container share by design.

### A contract read as a sequence: `[MultipleImplementations]`

When several modules implement a contract on purpose — listeners, seeders, handlers, resolved as
`IEnumerable<T>` — say so on the contract. Startup accepts any attribute named
`MultipleImplementationsAttribute`, in any namespace, so the contracts library declares its own
rather than referencing this package:

```csharp
[MultipleImplementations]
public interface IOrderStatusListener
{
    Task OnChangedAsync(OrderStatusChanged change, CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
internal sealed class MultipleImplementationsAttribute : Attribute;
```

On a generic type it covers every construction.

### A type you do not own: `FusionModules:AllowedServiceCollisions`

For a library's type, which you cannot annotate, list it in configuration, in any of the
[spellings](#naming-a-type) — a generic definition covers every construction:

```json
{
  "FusionModules": {
    "AllowedServiceCollisions": [ "MyLib.IExtensionPoint" ]
  }
}
```

Every host that loads both modules needs it, test hosts included — which is why the attribute is
the better answer for your own contracts.

### No switch to make it a warning

A collision always fails startup. A switch that made it a warning would answer for every type at
once, including the ones a later change would add. A collision you have to live with has a name:
put it in the allow-list above, which answers for that type and no other.

What can be switched off is the runtime checks as a whole — see
[Turning the runtime checks off](#turning-the-runtime-checks-off) — for a host that relies on
MOD0014 and its tests instead.

### At build time: MOD0014

[MOD0014](rules/MOD0014.md) asks the same question in the host's build, as a warning, before
anything runs. Each module's build records the services its own code registers; the host's build
compares them across every module it references. The two checks see different things:

| | MOD0014 (build) | Startup check |
|---|---|---|
| Which modules | every module the host references, together | the modules this topology loads |
| Registrations inside a library's extension method | not seen | seen |
| A `TryAdd` that lost to an earlier one | seen | not seen — it never reached the container |
| Factories, instances | seen as written in the module's code | seen as registered |
| Intended overlap on your own contract | `[MultipleImplementations]` | `[MultipleImplementations]` |
| Alternatives no topology loads together | `fusion_modules_allowed_service_collisions` | nothing: they never meet; if they do, startup fails and says they are alternatives |
| A type you do not own, read as a sequence | `fusion_modules_allowed_service_collisions` | `FusionModules:AllowedServiceCollisions` |

The startup check cannot read `.editorconfig`, and the build cannot read a deployment's
configuration, so a library's type that modules really share is listed in both.

## Two modules mapping one route

Two services could each answer `GET /health/details` or `POST /api/settings`. Merged, both endpoints are in
one route table, and nothing fails: the host starts, the probe passes, and the first request to that
address matches two endpoints and answers 500 with an `AmbiguousMatchException`. So once every module
has run `Configure` — the pipeline is built and the server is not listening yet — startup logs one
**warning** naming each such route and the modules that map it:

```text
Routes mapped by more than one module match the same request, and answer it with 500
(AmbiguousMatchException): GET /orders/{id} (Billing, Orders). Map them to different paths, or tell
them apart by HTTP method, host, content type or Order.
```

What counts as the same route is what the matcher uses: the template with parameter names ignored
(`/orders/{id}` and `/orders/{orderId}` are one route) but constraints, optional parameters and
catch-alls kept; a conventional route's required values (`{controller}/{action}` for `Home` and for
`Orders` are two routes); overlapping HTTP methods (an endpoint without methods overlaps every
method); and the same host, accepted content types and `Order`. Whose an endpoint is comes from what
serves it, written as the startup report writes it — `host` for the host's own: the
controller's assembly for an action — wherever the action is declared, so actions inherited from a base
controller in a shared library count for the module whose controller inherits them — the assembly a
Razor Page is compiled into, the assembly that declares a minimal API's delegate, and the service or hub type of a gRPC method or a SignalR hub. So the
host's own endpoints take part too, and so do a library's (`MapOpenApi`'s document collides with a
module's route all the same). Endpoints with none of these (health checks, a bare `RequestDelegate`)
are left out, and so are two endpoints of one owner.

It warns rather than fails: endpoints with one template are sometimes told apart by something the
comparison does not model, a policy or an endpoint filter. The fix is a `MapGroup` prefix or an MVC
area per module. The skill's `assess.ps1` looks for the same thing before the migration, from the source.

To compare the routes, the check reads the endpoint table, and that builds each minimal API's request
delegate — work ASP.NET Core otherwise does on the first request. A handler that cannot be built, because
it needs a service the topology does not have, would throw there. The check does not let that stop the
host: it logs that the endpoints could not be read and why, and the first request fails as it always would.
That line is worth reading too — it is the missing service, named at startup.

## Options several modules configure

The collision check leaves the framework's own contracts alone, and options are among them:
`JsonOptions`, `HubOptions`, `GrpcServiceOptions`, `RateLimiterOptions` are registered to be configured
by many callers. After the merge, though, there is one of each per process — every module's
`Configure` runs against the same instance, so one module's JSON converter, hub limit or gRPC
interceptor applies to its neighbours' endpoints too, and a value two modules both set is whichever
ran last.

So startup logs one **warning** when two modules configure the same options type with delegates of
their own:

```text
Options configured differently by more than one module apply to all of them:
JsonOptions (Catalog, Ordering); HubOptions (Notifications, Games). ...
```

What counts: the default, unnamed instance (`Configure<T>(…)`, `ConfigureAll`, `PostConfigure`),
configured by a delegate declared in the module's own assembly — the module's own settings. A delegate
declared anywhere else is a library's: the framework's, a package's (`AddSwaggerGen` adds an MVC
convention), or a helper the application wrote for all its modules, which runs the same code for each
caller. Those are not reported, and neither are named instances, which are how modules stay apart.
Counted, one module's `AddSwaggerGen` and a shared helper adding a convention scoped to each module's
own controllers would read as every module configuring MVC differently, on every start.

It warns rather than fails, because adding to shared options is often what modules mean. When it is
not, configure per hub, per endpoint or per named instance where the framework allows it
(`AddHubOptions<THub>`, a named authentication scheme); when sharing is intended, list the options type
under `FusionModules:AllowedServiceCollisions`.

## Naming a type

`FusionModules:AllowedServiceCollisions` accepts a type in any of these spellings:

| | Example |
|---|---|
| short name | `OrderFulfillment`, `IHandler<Order>`, `IHandler<Order, Invoice>` |
| full name | `Orders.OrderFulfillment`, `MyLib.IHandler<Order>`, `MyLib.IHandler<Shop.Order>` |
| metadata name | ``MyLib.IHandler`1`` |
| a nested type's full name | `Orders.Module+Worker`, as .NET writes it, or `Orders.Module.Worker`, as C# does |
| a generic definition, in any of the above | `IHandler<>`, `MyLib.IHandler<>`, `IHandler<,>` — every construction |

A short name matches every type of that name, in any namespace. Write the full one where that is
too broad.

## The startup report

Once per host, when the last module has run `Configure` — the endpoints are mapped and the server is
not listening yet — one `Information` line in the `FusionModules` category:

```text
Topology: modules: Catalog, Local, Orders.Entities, Ordering; endpoints: Catalog 14, Ordering 9,
host 2 (none from Local, Orders.Entities); hosted services: OrderFulfillmentService (Ordering),
OrderStatusDispatcher (Ordering); library hosted services: DataProtectionHostedService,
HealthCheckPublisherHostedService, TelemetryHostedService (registered by Catalog); shared on purpose:
IOrderStatusListener
```

Every part is written `label: value` and the parts are separated by `;`, in this order:

| Part | Field | What it says |
|---|---|---|
| `modules` | `Modules` | The loaded modules, in `GetLoadedModules` order: after the modules each references, otherwise by name. |
| `endpoints` | `Endpoints` | The endpoints each module serves, the host's own, and the modules that serve none. |
| `hosted services` | `HostedServices` | The modules' own hosted services, each with its module. |
| `library hosted services` | `LibraryHostedServices` | The framework's and libraries' hosted services the modules registered. |
| `shared on purpose` | `Shared` | Contracts several modules implement on purpose, marked or allowed. |

A part with nothing to say is left out of the line — a topology without workers has no
`hosted services:` part — rather than written as `none`. The modules, and the endpoints after them,
come in `GetLoadedModules` order, and every other list is sorted, so one topology writes one line
whatever order `HOSTINGSTARTUPASSEMBLIES` names its modules in.

The module list is decided by configuration — `HOSTINGSTARTUPASSEMBLIES` — and so differs between
deployments of one image. When a job runs twice, or not at all, read this line first. The last part
lists only contracts that several modules actually implement, not every type the allow-list mentions.

### Endpoints

Each module that serves endpoints is counted, by the same owner the route check uses: the
controller, the page, the minimal API's delegate, the gRPC service or the hub, declared in the
module's assembly. The host's own endpoints — whose handlers its own assembly declares,
`IHostEnvironment.ApplicationName` — are counted as `host`, whether or not that assembly is a module
too (it carries `[assembly: HostingStartup(…)]` of its own): such a host is `host` in every part of
the line — among the modules, beside its endpoints and its hosted services — so that the line does
not call it by two names, and moving the host's module into a project of its own does not rename
its endpoints. Only when a loaded module's assembly is itself named `host` is the host written under
the application's name instead. Endpoints whose
handler the framework or a library declares — health checks, `MapOpenApi`'s document, static assets
— are no module's and not the host's, and are not counted. FusionModules.Testing's `Routes()` names
owners the same way: `host` (`ModuleRoute.Host`) for the host's routes, `null` for these.

Modules that serve nothing are named apart: `(none from Local, Orders.Entities)`, or `none from …`
when no module serves anything. Most of them mean to — a transport, a module of entities, a module of
workers. It is suspicious only for a module meant to serve requests: that is the 404 this approach
keeps producing, an application part not added or a `UseEndpoints` not called. When the endpoints
cannot be read at startup (see above), the part says `not read`; with the
[runtime checks off](#turning-the-runtime-checks-off), `not read: runtime topology checks are off`.

### Hosted services

What the modules registered and the container still has once it is built: the host's own hosted
services are not in it, and neither is a worker the host or a test removed after the modules
registered it — `ConfigureTestServices`, FusionModules.Testing's `WithoutApplicationHostedServices` —
because it does not run.

One rule decides whose a hosted service is. Declared in the module's own assembly, it is listed under
`hosted services` with that module. Declared anywhere else, it is listed under `library hosted
services`. Such services are mostly added once, with `TryAddEnumerable`, by whichever module asked
first, and that module is not their owner:

- the framework's (the health check publisher, Data Protection's key ring loader) are listed by name
  alone. Which module asked first depends on the order of `HOSTINGSTARTUPASSEMBLIES`:
  `DataProtectionHostedService` read as registered by an MVC module in one deployment and by a Razor
  module in the next.
- a library's (OpenTelemetry's `TelemetryHostedService`) keep the module that registered it,
  `(registered by Catalog)`. A module may also choose to run a library's worker; the report cannot
  tell which, so it says both things it knows: whose code it is, and who registered it.

### Fields

Each part is also a field of its own in the log entry — `Modules`, `Endpoints`, `HostedServices`,
`LibraryHostedServices`, `Shared` — so a log store (Application Insights, Seq, the Aspire
dashboard) can filter on it, for instance for the replicas that run a given worker. Every field is
there on every replica: a part left out of the line is an empty field. The message template
(`{OriginalFormat}`) has only the parts in the line.

To silence it, raise the category's level: `"Logging": { "LogLevel": { "FusionModules": "Warning" } }`.

## The module registry

Each module writes `FusionModules:Modules:<AssemblyName>` = its type's assembly-qualified name into
configuration as it activates. That is what `ModuleBase.GetLoadedModules` reads, and what lets any
code holding an `IConfiguration` ask which modules are live without the host knowing about
modularity. It is written by the package; there is no reason to set it yourself — for code that runs
without a web host, `ModuleBase.CreateModuleRegistry` builds the same entries.
