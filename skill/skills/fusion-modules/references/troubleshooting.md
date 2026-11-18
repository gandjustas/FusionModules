# When it fails quietly

Almost every failure mode of this approach is silent. The application starts, the probe passes,
and something is simply missing. Work from the symptom.

## An endpoint returns 404 in a topology that should have it

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

## Startup fails: "Two modules register the same service with different implementations"

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

## A warning at startup: "Routes mapped by more than one module"

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

## `TypeLoadException` the first time a client connects to a hub

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

## Every gRPC call fails with `HTTP_1_1_REQUIRED` after the merge

The merged host's gRPC endpoint is plaintext and set to `Http1AndHttp2`. Without TLS there is no
ALPN, so the connection is HTTP/1.1 and gRPC refuses it. Set that endpoint's `Protocols` to
`Http2` — see [the host](host.md). The union of two services' settings is not a superset here.

## Startup throws: "EndpointRoutingMiddleware ... must be added ... before EndpointMiddleware"

The host never called `UseRouting()` and maps no endpoints of its own, so `WebApplication` had no
reason to add routing automatically. Add `app.UseRouting()`.

Loud rather than silent, which is why there is no analyzer rule for it: the runtime's message
already names the fix, and a rule would fire on every host that maps an endpoint itself and
therefore does not need the call.

## The application starts but a module's services are missing

The module activated but `ConfigureServices` did not register what the endpoint needs — usually
because the registration stayed behind in the original `Program.cs`. Compare against the service
list captured in Phase 0.

If the endpoint exists and throws on resolution, that is this. If the endpoint does not exist at
all, it is the previous section.

## A migration comes out empty, or covers the wrong modules

`dotnet ef` builds the host, so `HostingStartup` runs and the model follows
`HOSTINGSTARTUPASSEMBLIES` as set in the shell that ran the command: unset gives an empty model,
one topology's value gives that topology's tables. There is no error either way — `Up` is just
empty, or short. Generate migrations through a design-time factory that names every module with
`UseModules(KnownModules.Names)` and ignores the environment. See [data.md](data.md).

## A topology has tables it should not, or is missing tables it should have

EF Core's model cache, keyed by context type, shared across hosts in one process. The second
topology in a test run gets the first one's model. Nothing throws. `UseModules` from
`FusionModules.EntityFrameworkCore` puts a key that includes the module set in place; a context that
replaces `IModelCacheKeyFactory` itself after it loses that key and has to include
`context.GetModuleSetKey()` in its own — [data.md](data.md).

If it happens at runtime rather than in tests, look at what the model is composed from. An
`AppDomain.CurrentDomain.GetAssemblies()` scan reports the application's own modules correctly
while one host owns the process, but it also reports hosting startups that arrived with a
package.

## Controllers or pages appear in a topology that excluded their module

Application Part Discovery. The host has
`GenerateMvcApplicationPartsAssemblyAttributes` unset or true, so the SDK wired the module's
controllers in at build time, behind `HOSTINGSTARTUPASSEMBLIES`' back. MOD0007 reports it; the
fix is the property plus each module calling `AddApplicationPart` for itself.

## Startup fails: "requires X, which was not activated"

Correct behaviour. The module uses types from another module, so it depends on it. Either add
that module to the topology, or — if the dependency is only a shared DTO — move the type into a
contracts library so the reference goes away.

Do not silence this by removing the project reference: the module would then fail to load at all.

## Startup fails: "'X' could not be loaded"

`ValidateModules()` found a name in `HOSTINGSTARTUPASSEMBLIES` that is not loaded in the process:
a typo, or the assembly is not deployed next to the host. Check for
`ReferenceOutputAssembly="false"` on the project reference — it stops the module being copied to
the output, which looks like a routing problem until you look in the folder.

## Startup fails: "'X' is a module, but its startup did not run"

The assembly loaded and is one of the host's modules, but ASP.NET Core never ran it: look for
`HOSTINGSTARTUPEXCLUDEASSEMBLIES`, or `ASPNETCORE_PREVENTHOSTINGSTARTUP` set to `true`.

## Nothing at all happens and no module loads

The host does not call `ValidateModules()`, so nothing compares the variable with its modules —
MOD0012 says so at build time, and its code fix adds the call. Add it right after the builder is
created; until then, look at the log for ASP.NET Core's own
critical message about the first name it could not load.

## Every request answers 500 after a module was added

Something the process has one of was given twice. `Duplicate endpoint name '…'`: two modules use
one `WithName`. `An item with the same key has already been added. Key: v1`: two `SwaggerDoc`s.
`AmbiguousMatchException` on one path: two modules, or a module and a shared call the host also
makes, map it. See "Once per process" in [the host](host.md).

## Every request answers 404, the host's own included or not

No module loaded: the host was started without the launch profile or the variable that names them.
The `Topology:` line in the log has no `modules:` part.

## `/health` throws, or says Healthy too easily

`Duplicate health checks were registered with the name(s)` — checks registered without a `name:` by
several modules. If it answers Healthy, count the entries: only one module's chain may have
survived.

## A message is published and nothing handles it

In order: the consumer became internal and the scan takes public types; the scan was handed
`Assembly.GetExecutingAssembly()` and now runs in another assembly; the bus is configured once and
this module's consumers were never contributed to it; or two modules share one queue name and the
other one took the message.

## An API endpoint redirects to a login page instead of answering 401

The module's endpoints say `RequireAuthorization()` and the host's default scheme is a cookie. Give
them a named policy that names the bearer scheme — [modules.md](modules.md).

## Login loops after the identity service became a module

In order: the identity registration took over the host's default schemes; the provider does not
know which cookie scheme is the application's; or a cookie policy that ran before routing in the
service runs after it in the module, and over plain HTTP the sign-in cookie is dropped for its
`SameSite`. See [the host](host.md).

## A handler stopped running, or a type cannot be found by name, only in the merged host

`Assembly.GetEntryAssembly()` or `typeof(Program)` in the module or in a library it uses: the entry
assembly is the host's. Or a registry keyed by a short type name that several modules now share.
See "What a service assumed about being the process" in [modules.md](modules.md).

## Validation stopped happening after types were made internal

An assembly scan that takes public types only — FluentValidation without
`includeInternalTypes: true`. Nothing fails; requests pass unvalidated.

## The build cannot write a module's DLL (MSB3021, MSB3027)

Something still runs it: an Aspire AppHost left up, or a build server holding the file. Stop the
AppHost, `dotnet build-server shutdown`, and give verification builds their own output directory.

## A package was rebuilt under the same version and nothing changed

NuGet restored the copy in its global packages folder. Delete that version's folders there —
`dotnet nuget locals global-packages --list` says where it is, and `NUGET_PACKAGES` may have moved
it — then `dotnet restore <solution> --force` and `dotnet tool restore`.

## Behaviour changed after merging, in a way nobody can pin down

The usual suspects, in order:

1. **Middleware order.** Each service had its own; now there is one. Phase 0's diff table says
   which ones differed.
2. **A configuration key defined by two modules** with different values. Last write wins.
3. **One service registered with `TryAdd` in two modules.** Startup fails when two modules both
   add a service, but a `TryAdd` that added nothing leaves no trace: the module activated first
   wins, and the other is given its implementation.
4. **A hosted service now running in more replicas than before**, or more than once per topology.
5. **A retry or circuit breaker around what is now a local call**, changing failure behaviour.

## The diagnostics

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
| MOD0015 | Info, not a defect: this program's own registrations and mappings can move into a module in the same project. The fix writes `Module.cs`; run it with `dotnet format analyzers <solution> --diagnostics MOD0015 --severity info`. See [modules.md](modules.md). |
| MOD0016 | Info, not a defect: entity configuration in `OnModelCreating` can become `IEntityTypeConfiguration<T>` classes and `DbSet` properties extension members of `DbContext`. Same command with `MOD0016`; compare inventories before and after. See [data.md](data.md). |

MOD0015 and MOD0016 are suggestions, so `dotnet build -warnaserror` does not fail on them and
"zero MOD diagnostics" does not mean them. If `dotnet format` changed nothing: `--severity info` is
missing, the path was given in `/c/...` form from Git Bash, or the project's `obj` is stale and it
loaded without the analyzer (`dotnet restore --force`). If the build then fails with NETSDK1022, the
project consumes the analyzers without the package's build targets: delete the `<Compile Include>`
lines `dotnet format` added.

Full text for each: `docs/rules/MOD0001.md` and siblings in the repository.
