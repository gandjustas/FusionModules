# 02 — MVC and Razor Pages modules

Views, areas, page models and static assets, all inside modules the host has never heard of.

```bash
cd Host
dotnet run --launch-profile DashboardOnly   # /Dashboard   (MVC area)
dotnet run --launch-profile StatusOnly      # /status      (Razor Pages)
dotnet run --launch-profile AllModules      # both
```

## The one thing that would otherwise go wrong

Application Part Discovery and HostingStartup do not know about each other.

At build time the Razor SDK emits `[assembly: ApplicationPart("X")]` on a project for every
project reference that itself references `Microsoft.AspNetCore.Mvc.*`. At runtime
`AddControllers` / `AddRazorPages` read those attributes and load the named assemblies. So the
host would pick up a module's controllers and pages **without ever running the module's code** —
the routes appear, the services they depend on do not, and they appear in every topology,
including the ones that deliberately left the module out.

Modulith's MSBuild assets set `GenerateMvcApplicationPartsAssemblyAttributes` to false once the
project is detected as a host, and each module calls `AddApplicationPart` for itself inside its
own `ConfigureServices` — where it also registers everything its controllers need. MOD0004 is the
backstop if someone puts the attribute back.

`ViewModuleTests.NoModules_ServesNoControllersAndNoPages` is the test that would fail if any of
that came undone.

## Project shape

A module with `.cshtml` files uses `Microsoft.NET.Sdk.Razor`; one without needs only
`Microsoft.NET.Sdk`. `AddRazorSupportForMvc` is required for Razor Pages modules as much as for
MVC ones, despite the property's name — Modulith sets it, rather than leaving every author to hit
RAZORSDK1004 once and remember it forever.

## Static assets

A module's `wwwroot` is served from `_content/<AssemblyName>/`, so two modules can both ship
`site.css` without knowing about each other — see the `<link>` in
[`Status.cshtml`](Modules/Status/Pages/Status.cshtml). The host only needs `MapStaticAssets()`.
