# How it works

Nothing here is a plugin engine. Every mechanism is stock ASP.NET Core, and knowing exactly what
it does is the difference between a migration that works and one that fails in ways nobody can
explain.

## Activation

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

## ModuleBase

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

## The registry

Each module writes itself into configuration as it activates:

```
FusionModules:Modules:<AssemblyName> = <assembly-qualified name of the module type>
```

`ModuleBase.GetLoadedModules(IConfiguration)` reads it back, in activation order. This is how
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
it.

## Dependencies between modules

A module that uses another module's types depends on it. Nothing declares that: the compiler only
emits an assembly reference for an assembly whose types are actually used, so the reference list
is exact. `ModuleBase` checks at startup that every referenced module was activated and fails
loudly if one was not.

Which means a reference is a statement about deployment. If module A uses one DTO from module B,
every topology containing A must also contain B. When that is not what you want, the type belongs
in a **contracts library**: a plain `Microsoft.NET.Sdk` project with no `[HostingStartup]`, which
is therefore not a module and whose types are meant to be public.

### The limit, stated plainly

The "module did not activate" check runs from the modules that *did* activate. If every name in
`HOSTINGSTARTUPASSEMBLIES` is misspelled, nothing loads and nothing notices. Closing that would
mean the host knowing it has modules, which is the property the whole model exists to preserve.
MOD0005 covers the same class of mistake at build time.

## Project shapes

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
presents as a routing problem. MOD0003 is the enforcement mechanism, not MSBuild.

## MVC and Razor Pages

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

## What this costs

Modules load by name at runtime, so the trimmer cannot see that they are used:
`PublishTrimmed` and `PublishAot` are not available for the host. Say so before the user finds
out during a deployment.
