# When it fails quietly

Almost every failure mode of this approach is silent. The application starts, the probe passes,
and something is simply missing. Work from the symptom.

## An endpoint returns 404 in a topology that should have it

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

MOD0009 catches this at build time. It is worth knowing what survives it otherwise: a clean build,
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
`ModuleBase.CreateModuleRegistry(...)` and ignores the environment. See [data.md](data.md).

## A topology has tables it should not, or is missing tables it should have

EF Core's model cache, keyed by context type, shared across hosts in one process. The second
topology in a test run gets the first one's model. Nothing throws. Fix with an
`IModelCacheKeyFactory` that includes the module set — [data.md](data.md).

If it happens at runtime rather than in tests, look at what the model is composed from. An
`AppDomain.CurrentDomain.GetAssemblies()` scan reports the application's own modules correctly
while one host owns the process, but it also reports hosting startups that arrived with a
package.

## Controllers or pages appear in a topology that excluded their module

Application Part Discovery. The host has
`GenerateMvcApplicationPartsAssemblyAttributes` unset or true, so the SDK wired the module's
controllers in at build time, behind `HOSTINGSTARTUPASSEMBLIES`' back. MOD0004 reports it; the
fix is the property plus each module calling `AddApplicationPart` for itself.

## Startup fails: "requires X, which was not activated"

Correct behaviour. The module uses types from another module, so it depends on it. Either add
that module to the topology, or — if the dependency is only a shared DTO — move the type into a
contracts library so the reference goes away.

Do not silence this by removing the project reference: the module would then fail to load at all.

## Startup fails: "listed in HOSTINGSTARTUPASSEMBLIES but could not be loaded"

A typo, or the assembly is not deployed next to the host. Check for
`ReferenceOutputAssembly="false"` on the project reference — it stops the module being copied to
the output, which looks like a routing problem until you look in the folder.

## Nothing at all happens and no module loads

If *every* name in the variable is wrong, there is no module left to notice, and the check cannot
run. Look at the log for ASP.NET Core's own critical message about the first name it could not
load.

## Behaviour changed after merging, in a way nobody can pin down

The usual suspects, in order:

1. **Middleware order.** Each service had its own; now there is one. Phase 0's diff table says
   which ones differed.
2. **A configuration key defined by two modules** with different values. Last write wins.
3. **A non-`TryAdd` DI registration in two modules.** The winner depends on the order of the
   environment variable, which means it can differ between topologies.
4. **A hosted service now running in more replicas than before**, or more than once per topology.
5. **A retry or circuit breaker around what is now a local call**, changing failure behaviour.

## The diagnostics

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
| MOD0009 | A hub's client interface is not visible outside the module, so SignalR's generated proxy cannot implement it. Grant the proxy's assembly access to internals, or make the interface public. |
| MOD0020 | The FusionModules package is not referenced, so the rules that need `ModuleBase` are inactive and the build is green because nothing is being checked. |

Full text for each: `docs/rules/MOD0001.md` and siblings in the repository.
