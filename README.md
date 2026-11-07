# FusionModules

A modular monolith for ASP.NET Core — with no framework.

A module is an ordinary class library. It is activated by naming its assembly in
`HOSTINGSTARTUPASSEMBLIES`. One image, any deployment topology, chosen by an environment variable
rather than a rebuild. ASP.NET Core has had every mechanism for this for years; this package is
one base class over them, plus the analyzers that stop the architecture from leaking.

```csharp
// OrdersModule/Module.cs — the whole module contract
[assembly: HostingStartup(typeof(Module))]

class Module : ModuleBase
{
    protected override void ConfigureServices(IHostApplicationBuilder builder)
        => builder.Services.AddScoped<IOrderService, OrderService>();

    protected override void Configure(IApplicationBuilder app)
        => app.UseEndpoints(e => e.MapGroup("/orders").MapGet("/unpaid", (IOrderService s) => s.Unpaid()));
}
```

```csharp
// the host — note that it knows nothing about any module
// (ValidateModules is generated from its references and checks HOSTINGSTARTUPASSEMBLIES)
var builder = WebApplication.CreateBuilder(args).ValidateModules();
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
dotnet add package FusionModules
```

That is the whole dependency. The analyzers come with it. `net8.0` and `net10.0`.

## Why a package at all

Because the approach has sharp edges, and every one of them fails quietly:

- A module that forgets `[assembly: HostingStartup]` loads and does nothing. The application
  starts, the readiness probe passes, and the first sign is a 404. **MOD0003** makes it a build
  error.
- A typo in `HOSTINGSTARTUPASSEMBLIES` makes ASP.NET Core log a critical message and carry on
  starting. `builder.ValidateModules()`, generated from the host's references, fails startup
  instead — every name has to load, and every one of the host's modules has to have run.
- The Razor SDK wires a module's controllers into the host behind `HOSTINGSTARTUPASSEMBLIES`'
  back, so they appear in topologies that excluded the module. The MSBuild assets turn that off;
  **MOD0007** is the backstop.
- The host using one type from one module quietly puts that module in every deployment.
  **MOD0004** catches it while allowing the project reference the model needs.
- A `Hub<TClient>` whose client interface is internal — which is what every other rule here asks
  for — compiles, passes its tests and throws `TypeLoadException` the first time a client
  connects. **MOD0010** asks the question at build time.

And one thing it makes possible rather than prevents: MVC requires a controller or a view component
to be public, which drags its constructor's services and its actions' models public with it, until
most of the module is. `AllowInternalControllers()` and `AllowInternalViewComponents()` lift that
requirement. Razor Pages never had it, and tag helpers cannot be relieved of it —
[MOD0001](docs/rules/MOD0001.md) says which is which and why.

## Rules

| | |
|---|---|
| [MOD0001](docs/rules/MOD0001.md) | A module must not expose public types |
| [MOD0002](docs/rules/MOD0002.md) | The type named by HostingStartup must be a module |
| [MOD0003](docs/rules/MOD0003.md) | A module must be named by an assembly-level HostingStartup attribute |
| [MOD0004](docs/rules/MOD0004.md) | The host must not use types from a module |
| [MOD0005](docs/rules/MOD0005.md) | A library that is not a module must not use types from a module |
| [MOD0006](docs/rules/MOD0006.md) | The host's builder does not load modules |
| [MOD0007](docs/rules/MOD0007.md) | The host must not declare an ApplicationPart for a module |
| [MOD0008](docs/rules/MOD0008.md) | A module must not replace the application pipeline |
| [MOD0009](docs/rules/MOD0009.md) | A module must not register itself as a startup filter |
| [MOD0010](docs/rules/MOD0010.md) | A hub's client interface must be reachable from SignalR's generated proxy |
| [MOD0011](docs/rules/MOD0011.md) | MVC never discovers the module's controllers or pages |
| [MOD0012](docs/rules/MOD0012.md) | The host does not validate its modules |
| [MOD0013](docs/rules/MOD0013.md) | The FusionModules package is not referenced |

A host and a test project also get `KnownModules`, generated from their references: every module,
by name. It is what an `IDesignTimeDbContextFactory` needs — `dotnet ef` takes
the model from `HOSTINGSTARTUPASSEMBLIES` as it stood in your shell, so a factory names the modules
itself, and a list written by hand is a copy of the project file that nothing keeps honest.
With it comes `ValidateModules()`, an extension on the builder that checks `HOSTINGSTARTUPASSEMBLIES`
against that list at startup.

The package also sets a few MSBuild properties, each of them a fix for something that fails
quietly. What they do, what is generated and how to override either: [docs/msbuild.md](docs/msbuild.md).

## Samples

| | |
|---|---|
| [01 — Minimal API](samples/01-minimal-api) | The smallest thing that shows the idea |
| [02 — MVC and Razor Pages](samples/02-mvc-razor) | Views, areas, page models and per-module static assets |
| [03 — Modular data model](samples/03-modular-data) | One EF Core model composed from modules; three topologies from one image |
| [04 — SignalR](samples/04-signalr) | A hub with a typed client, and the one failure a build, a test suite and a route inventory can all miss |
| [05 — Startup work](samples/05-startup) | The lines a service ran between Build() and RunAsync(), once it is a module |
| [06 — Transports](samples/06-transports) | One edge with the network hop present and absent, and one suite proving both |

## Migrating an existing system

[`skill/`](skill) holds instructions for an AI agent doing the migration: assess, host, modules,
data, transports, deployment, and a verification loop that runs after every phase rather than at
the end. The same files are a Claude Code plugin and an [Agent Skills](https://agentskills.io)
directory that Codex, OpenCode, Copilot, Cursor, Gemini CLI and ZCode load as they are; a
flattened [`SKILL.md`](skill/dist/SKILL.md) and a short always-on rules file cover everything
else.

In Claude Code:

```
/plugin marketplace add gandjustas/FusionModules
/plugin install fusion-modules@fusion-modules
```

Any other agent, and the other ways in: [skill/README.md](skill/README.md#using-it).

It is also worth reading as prose if you are doing it by hand — particularly
[troubleshooting](skill/skills/fusion-modules/references/troubleshooting.md), which lists the
failure modes of this approach by symptom, because every one of them is silent.

## What is deliberately not here

An EF Core package, a testing package, project templates, a messaging abstraction. Each of them
would be a handful of lines wrapped in something you have to depend on, version and learn — and
the claim this project is making is that the approach does not need a framework. They live in the
samples as code to copy, with the reasoning next to them:
[the data model recipe](samples/03-modular-data#the-recipe),
[the testing recipe](samples/03-modular-data#tests).

Modules also load by name at runtime, so `PublishTrimmed` and `PublishAot` are off the table for
the host. That is the cost of the approach and it is worth knowing before you adopt it.

## Upgrading from 0.1

0.2 numbers the rules by severity — errors first, then warnings — and that moves most of them.
**Every `#pragma warning disable`, `<NoWarn>` and `.editorconfig` severity that names a rule has to
be renumbered**, or it silently applies to a different rule:

| 0.1 | 0.2 | Rule |
|---|---|---|
| MOD0001 | MOD0001 | A module must not expose public types — controllers included now |
| MOD0002 | MOD0002 | The type named by HostingStartup must be a module — now with a code fix |
| MOD0005 | MOD0003 | A module must be named by an assembly-level HostingStartup attribute |
| MOD0003 | MOD0004 | The host must not use types from a module |
| — | MOD0005 | A library that is not a module must not use types from a module |
| — | MOD0006 | The host's builder does not load modules (`CreateSlimBuilder`, `CreateEmptyBuilder`) |
| MOD0004 | MOD0007 | The host must not declare an ApplicationPart for a module |
| MOD0006 | MOD0008 | A module must not replace the application pipeline |
| MOD0007 | MOD0009 | A module must not register itself as a startup filter — was "Redundant IStartupFilter registration"; now an error, only for the module itself, with a code fix |
| MOD0009 | MOD0010 | A hub's client interface must be reachable from SignalR's generated proxy |
| — | MOD0011 | MVC never discovers the module's controllers or pages — with a code fix |
| — | MOD0012 | The host does not validate its modules |
| MOD0020 | MOD0013 | The FusionModules package is not referenced |
| MOD0008 | — | A hosted service in a module: removed. Delete its suppressions — under 0.2, MOD0008 is an error |

Besides the numbers:

- A module overrides `ConfigureServices(IHostApplicationBuilder)`. The
  `ConfigureServices(WebHostBuilderContext, IServiceCollection)` overload still runs, after it, but is
  obsolete; the code fix for CS0672 moves its body across.
- A module's controllers are internal, like the rest of it: MOD0001 reports a public one and its
  fix makes it internal, and MOD0011 reports a module that then never calls
  `AllowInternalControllers()` — its fix writes the call.
- `GetLoadedModules` returns the host's own assembly first, as before, then each module after the
  modules it references, and by name otherwise, whatever order `HOSTINGSTARTUPASSEMBLIES` lists them
  in; a composing module no longer has to be listed last.
- The host checks `HOSTINGSTARTUPASSEMBLIES` itself: call the generated `builder.ValidateModules()`
  where the builder is created. MOD0012 says where it is missing, and its code fix adds it.

## Status

Early development, pre-1.0. The public surface is two classes and nine members, locked by
`PublicAPI.Shipped.txt` so that adding to it is a reviewable change.

On [nuget.org](https://www.nuget.org/packages/FusionModules). Pushing a `v*` tag makes
`release.yml` build, test, pack, consume the package from a scratch project outside the repository,
attach it to the GitHub release and push it to nuget.org with
[Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the job
exchanges a GitHub OIDC token for an API key that lives an hour, so there is no `NUGET_API_KEY`
secret here to leak, rotate or hand to a fork. The rules and the public API of each release are
recorded in `AnalyzerReleases.Shipped.md` and `PublicAPI.Shipped.txt`.

To try a change before it is released, install from a local feed:

```bash
dotnet pack src/FusionModules/FusionModules.csproj -c Release -o local-feed
```

## Building this repository

```bash
dotnet build FusionModules.slnx -c Release -warnaserror && dotnet test --solution FusionModules.slnx -c Release --no-build
```

The samples are a second solution — `samples/Samples.slnx` — built and tested the same way.

One script is worth knowing about. In-repo builds prove nothing about packaging: analyzers and
MSBuild assets are wired up by project reference there, and the `analyzers/dotnet/cs` and
`buildTransitive` layout is only exercised once the package is restored. When that layout is wrong
the build stays green and the rules simply never run, so there is a job that packs the package and
consumes it from a scratch project outside the repository:

```bash
bash eng/pack-validate.sh
```

It needs a POSIX shell — git-bash on Windows, where it is also exercised in CI.

[`gandjustas/dotnext-2026`](https://github.com/gandjustas/dotnext-2026) does exactly that on its
`fusionmodules` branch, which is where this package gets used by something it did not grow up
inside.

## Origin

Extracted from the DotNext talk *Modularity without Microservices*
([sources](https://github.com/gandjustas/dotnext-2026)).

## License

MIT
