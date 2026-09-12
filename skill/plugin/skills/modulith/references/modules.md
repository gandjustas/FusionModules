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

Then register it with the host and with the test project:

```xml
<ProjectReference Include="..\Modules\Orders\OrdersModule.csproj" ModulithModule="true" />
```

Both, not just the host: the metadata is what generates `KnownModules`, and tests are where
module names are written down most often.

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

## Views and static assets

A module ships its own `Areas/`, `Pages/` and `wwwroot/`. It adds itself as an application part
inside `ConfigureServices`; the host must not, and Modulith turns off the SDK behaviour that
would have done it silently. Static assets are served from `_content/<AssemblyName>/`, so two
modules can both ship `site.css`.

## Per-module gate

Before moving to the next module:

1. `dotnet build -warnaserror` — no MOD diagnostics.
2. The route inventory for the topology containing this module matches what the service served
   before. See [verify.md](verify.md).
3. One smoke test for the module alone, and one for the full topology.
4. Commit.
