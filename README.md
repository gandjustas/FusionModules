# Modulith

A modular monolith for ASP.NET Core — with no framework.

[Русская версия](README.ru.md)

A module is an ordinary class library. It is activated by naming its assembly in
`HOSTINGSTARTUPASSEMBLIES`. One image, any deployment topology, chosen by an environment variable
rather than a rebuild. ASP.NET Core has had every mechanism for this for years; this package is
one base class over them, plus the analyzers that stop the architecture from leaking.

```csharp
// OrdersModule/Module.cs — the whole module contract
[assembly: HostingStartup(typeof(Module))]

class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
        => services.AddScoped<IOrderService, OrderService>();

    protected override void Configure(IApplicationBuilder app)
        => app.UseEndpoints(e => e.MapGroup("/orders").MapGet("/unpaid", (IOrderService s) => s.Unpaid()));
}
```

```csharp
// the host — note that it knows nothing about any module
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseRouting();
app.Run();
```

```yaml
# same image, three services
orders:    { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;OrdersModule" } }
customers: { environment: { HOSTINGSTARTUPASSEMBLIES: "Customers.Entities;CustomersModule" } }
monolith:  { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;Customers.Entities;Monolith" } }
```

## Install

```
dotnet add package Modulith
```

That is the whole dependency. The analyzers come with it. `net8.0` and `net10.0`.

## Why a package at all

Because the approach has sharp edges, and every one of them fails quietly:

- A module that forgets `[assembly: HostingStartup]` loads and does nothing. The application
  starts, the readiness probe passes, and the first sign is a 404. **MOD0005** makes it a build
  error.
- A typo in `HOSTINGSTARTUPASSEMBLIES` makes ASP.NET Core log a critical message and carry on
  starting. `ModuleBase` fails startup instead.
- The Razor SDK wires a module's controllers into the host behind `HOSTINGSTARTUPASSEMBLIES`'
  back, so they appear in topologies that excluded the module. The MSBuild assets turn that off;
  **MOD0004** is the backstop.
- The host using one type from one module quietly puts that module in every deployment.
  **MOD0003** catches it while allowing the project reference the model needs.
- Module names are strings in an environment variable, so a rename is found in production.
  `KnownModules` is generated from your project references and makes it a compile error.

## Rules

| | |
|---|---|
| [MOD0001](docs/rules/MOD0001.md) | A module must not expose public types |
| [MOD0002](docs/rules/MOD0002.md) | The type named by HostingStartup must be a module |
| [MOD0003](docs/rules/MOD0003.md) | The host must not use types from a module |
| [MOD0004](docs/rules/MOD0004.md) | The host must not declare an ApplicationPart for a module |
| [MOD0005](docs/rules/MOD0005.md) | A module must be named by an assembly-level HostingStartup attribute |
| [MOD0006](docs/rules/MOD0006.md) | A module must not replace the application pipeline |
| [MOD0007](docs/rules/MOD0007.md) | Redundant IStartupFilter registration |
| [MOD0008](docs/rules/MOD0008.md) | A hosted service in a module runs in every replica that loads it |
| [MOD0020](docs/rules/MOD0020.md) | The Modulith package is not referenced |

Diagnostics are available in English and Russian.

## Samples

| | |
|---|---|
| [01 — Minimal API](samples/01-minimal-api) | The smallest thing that shows the idea |
| [02 — MVC and Razor Pages](samples/02-mvc-razor) | Views, areas, page models and per-module static assets |
| [03 — Modular data model](samples/03-modular-data) | One EF Core model composed from modules; three topologies from one image |

## Migrating an existing system

[`skill/`](skill) holds instructions for an AI agent doing the migration: assess, host, modules,
data, transports, deployment, and a verification loop that runs after every phase rather than at
the end. Authored once and generated into a Claude Code plugin, a portable
[`SKILL.md`](skill/dist/SKILL.md) for any other tool, and a short always-on rules file.

It is also worth reading as prose if you are doing it by hand — particularly
[troubleshooting](skill/src/references/troubleshooting.md), which lists the failure modes of this
approach by symptom, because every one of them is silent.

## What is deliberately not here

An EF Core package, a testing package, project templates, a messaging abstraction. Each of them
would be a handful of lines wrapped in something you have to depend on, version and learn — and
the claim this project is making is that the approach does not need a framework. They live in the
samples as code to copy, with the reasoning next to them:
[the data model recipe](samples/03-modular-data#the-recipe),
[the testing recipe](samples/03-modular-data#tests).

Modules also load by name at runtime, so `PublishTrimmed` and `PublishAot` are off the table for
the host. That is the cost of the approach and it is worth knowing before you adopt it.

## Status

Early development, pre-1.0. The public surface is one class and six members, locked by
`PublicAPI.Shipped.txt` so that adding to it is a reviewable change.

Not published: the package id on nuget.org is not claimed, and claiming it has no undo while the
name is still open — `Modulith` is crowded there and collides with Spring Modulith. `release.yml`
packs, validates and attaches the artefact to a GitHub release; wiring up the push is one job
away and one decision away. Install from a local feed in the meantime:

```bash
dotnet pack src/Modulith/Modulith.csproj -c Release -o local-feed
```

[`gandjustas/dotnext-2026`](https://github.com/gandjustas/dotnext-2026) does exactly that on its
`modulith-packages` branch, which is where this package gets used by something it did not grow up
inside.

## Origin

Extracted from the DotNext talk *«Модульность без микросервисов»*
([sources](https://github.com/gandjustas/dotnext-2026)).

## License

MIT
