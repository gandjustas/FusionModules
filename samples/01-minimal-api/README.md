# 01 — Minimal API modules

The smallest thing that shows the idea.

Two modules, a host that knows about neither, and four launch profiles that differ only by an
environment variable.

```bash
cd Host
dotnet run --launch-profile NoModules     # /
dotnet run --launch-profile WeatherOnly   # / and /weather
dotnet run --launch-profile AllModules    # / and /weather and /greeting
```

## What to look at

**[`Host/Program.cs`](Host/Program.cs)** — six lines, and none of them mention a module. It is not
a plugin host; it is an ASP.NET Core application that happens to have `UseRouting()` in it.

**[`Modules/Weather/Module.cs`](Modules/Weather/Module.cs)** — `ConfigureServices` and `Configure`,
the same shape as the `Startup` class everybody already knows, plus one assembly attribute. The
module registers a service and maps an endpoint group; nothing in it is public.

**[`Modules/Greeting/Module.cs`](Modules/Greeting/Module.cs)** — a module can contribute
configuration too, so a default value can live with the feature that needs it.

**[`Host/Host.csproj`](Host/Host.csproj)** — the host references both modules and uses neither.
The reference is what orders the build and copies the assemblies next to the executable so they
can be loaded by name; MOD0004 is what stops it becoming a dependency.

**[`Tests/`](Tests)** — one topology per test, each a `ModularWebApplicationFactory<Program>` from
[FusionModules.Testing](../../docs/testing.md) given the modules it loads. The module names live in
one place as plain constants: they are the same strings a deployment writes into
`HOSTINGSTARTUPASSEMBLIES`, so they are not something the compiler can check, and a typo in one
fails startup with a message naming the module. The host calls `ValidateModules()`, generated from
its references, and that is what fails it — even when every name is misspelled and no module loaded
to notice.
