# Phase 2 — Into modules

One service or one feature at a time, lowest fan-in first: convert the things nothing else calls,
then the things that called them. Each conversion ends with a passing build, a passing route
inventory and a commit. Never have two half-converted modules at once — when something breaks you
want one suspect.

## The project

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

The SDK line is what bites first. `Microsoft.NET.Sdk.Web` contributes implicit usings that a plain
`Microsoft.NET.Sdk` project does not, so the first build of a converted service is a wall of CS0246
on types that are sitting right there in the framework reference. It reads like a missing package
reference and it is not one. Modulith puts them back for a module project, so on a current version
there is nothing to do; on an older one, add them yourself:

```xml
<ItemGroup>
  <Using Include="Microsoft.AspNetCore.Builder" />
  <Using Include="Microsoft.AspNetCore.Http" />
  <Using Include="Microsoft.AspNetCore.Routing" />
  <Using Include="Microsoft.Extensions.Hosting" />
</ItemGroup>
```

Then register it with the host and with the test project:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" />
```

In the test project as well as the host, so the module's assembly lands in the test output and
can be loaded there.

## Program.cs into Module.cs

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

## When a registration wants the builder, not the services

A good deal of modern .NET is written as extensions of `IHostApplicationBuilder` rather than of
`IServiceCollection` — every Aspire client integration, and most component packages. Those are not
decoration over `AddDbContext`: they resolve `ConnectionStrings:<name>` out of configuration,
register a health check and instrument the calls. Registering the client by hand keeps none of it.

Override the other `ConfigureServices` and call them unchanged:

```csharp
protected override void ConfigureServices(IHostApplicationBuilder builder)
{
    builder.AddNpgsqlDataSource("orders");
    builder.AddRedisClient("cache");
}
```

It is the module's view of the host rather than the host: the configuration is readable and closed
to new sources — `ConfigureAppConfiguration` is where those go, and adding one here throws rather
than disappearing — and the container is not a module's choice. Both overloads always run, and this
one runs first, so an explicit registration in the other still beats an integration's `TryAdd`.

## Five collisions, all silent

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

## Visibility

Build. MOD0001 will report every public type. Apply the "make internal" fix — Fix All handles a
converted service in one pass.

Exempt by default, because the framework finds them by reflection: controllers, page models, view
components, tag helpers, SignalR hubs and the client interface of a `Hub<TClient>`, and entity
types configured by an `IEntityTypeConfiguration<T>` in the same assembly.

Controllers need a second sentence, because the exemption is true and not enough. A public class
cannot have a public constructor taking an internal service, nor a public action returning an
internal DTO — so a public controller drags its services and its models public with it, and then
whatever those name. Keep the controller internal instead and let discovery see it:

```csharp
services.AddControllers()
    .AddApplicationPart(typeof(Module).Assembly)
    .AllowInternalControllers();
```

The class becomes `internal`, the constructor stays public — MVC's activator reads
`GetConstructors()`, which is public-only — and from there the parameters and return types may be
internal, because C# bounds a member's effective accessibility by its containing type. One call
covers the whole application.

The other three kinds the framework finds by reflection each answer differently, and guessing costs
a day:

- **View components** — `.AllowInternalViewComponents()`, same call site. Invoke by name or through
  the generic overload; both resolve at run time and both work. The one form that does not is
  `<vc:…>`, which the Razor compiler binds and which needs a public type — finding none, it reports
  nothing and copies the element into the page as literal HTML.
- **Razor Pages** — nothing to do. Page discovery reads the attributes the Razor compiler emits
  rather than scanning types, and the generated page class is itself internal, so an internal
  `PageModel` already works. Keep its constructor and handler methods public.
- **Tag helpers** — keep them public. `@addTagHelper` binds in the compiler against Roslyn symbols
  and requires public, for the current assembly as much as for referenced ones. An internal tag
  helper produces no descriptor at all: the element renders as literal HTML, with no error, no
  warning and nothing at runtime. Add it to the list of things the route inventory cannot see.

A hub's client interface needs a second sentence for the opposite reason: MOD0001 leaves it alone,
but SignalR still has to reach it. The proxy for a `Hub<TClient>` is generated into a dynamic
assembly of its own, which cannot implement an `internal` interface declared in yours — and nothing
says so until a client connects and the process throws `TypeLoadException`. MOD0009 asks the
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
modulith_allowed_public_types = OrderDto, PaymentEnvelope
```

Write down why. Nobody can tell a considered exception from an abandoned one.

## Views and static assets

A module ships its own `Areas/`, `Pages/` and `wwwroot/`. It adds itself as an application part
inside `ConfigureServices`; the host must not, and Modulith turns off the SDK behaviour that
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

## Per-module gate

Before moving to the next module:

1. `dotnet build -warnaserror` — no MOD diagnostics.
2. The route inventory for the topology containing this module matches what the service served
   before. See [verify.md](verify.md).
3. One smoke test for the module alone, and one for the full topology.
4. Commit.
