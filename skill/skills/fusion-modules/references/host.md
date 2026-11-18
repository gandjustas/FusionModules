# Phase 1 — The host

The host is the part that must stay ignorant. Everything else is recoverable; a host that knows
about a module has given up the property the whole approach exists for.

## Monolith → modules: do not create one

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

## Microservices → modules: a new, empty one

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

## What belongs in the host

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

## Kestrel endpoints

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

## An existing application as the host

Sometimes the answer to "which host" is the front end the services stood behind. Then the host is
not empty, and three things it already does now apply to every module:

- **Its default authentication scheme.** A storefront signs in with cookies; the APIs expected
  bearer tokens. Pin the host's defaults explicitly and give module endpoints a named policy
  ([modules.md](modules.md)).
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

## Cross-cutting concerns, reconciled once

Phase 0 produced a diff table of middleware order and cross-cutting configuration across the
services. Reconcile it now, before any module is converted, and write the decisions down. Doing
it later means re-testing every module that has already moved.

The order of the host pipeline applies to every module. Where two services disagreed, one of them
is going to change behaviour — decide which, deliberately.

### Once per process

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

### What stays out, and what nothing updates

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
[troubleshooting](troubleshooting.md).

## Launch profiles

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

## Gate

`dotnet run` starts and returns 404 for everything (or, in the monolith direction, behaves
exactly as it did before). `dotnet build -warnaserror` is clean. Only then start Phase 2.
