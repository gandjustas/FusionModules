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
    <PackageReference Include="FusionModules" Version="..." />
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
reference and it is not one. FusionModules puts them back for a module project, so on a current
version there is nothing to do; on an older one, add them yourself:

```xml
<ItemGroup>
  <Using Include="Microsoft.AspNetCore.Builder" />
  <Using Include="Microsoft.AspNetCore.Http" />
  <Using Include="Microsoft.AspNetCore.Routing" />
  <Using Include="Microsoft.Extensions.Hosting" />
</ItemGroup>
```

The same line takes five analyzers away, and that one is silent in both directions — the build
stays green and the rules simply stop asking. They live in the Web SDK's own folder rather than in
the framework reference: the Startup analyzer (ASP0000, ASP0001) and MVC1000 through MVC1006, which
is the set an MVC or Razor Pages module actually needs. FusionModules restores those too. The ASP0xxx
rules about minimal APIs and routing come with the framework reference and never left, so do not
read a quiet build as proof that the rest is still on — check `@(Analyzer)` if you are on an older
version:

```bash
dotnet build path/to/Module.csproj -getItem:Analyzer
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
    protected override void ConfigureServices(IHostApplicationBuilder builder)
    {
        // everything that was builder.* in Program.cs, unchanged:
        // builder.Services.Add…, builder.AddNpgsqlDataSource(…), builder.Configuration[…]
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

## Let MOD0015 write the first draft

Do not do that move by hand for a whole service. MOD0015 (Info) marks a program whose own
registrations and endpoint mappings can become a module **in the same project**, and its fix writes
`Module.cs`. The project stays an executable and runs as before — ASP.NET Core runs the entry
assembly's own `[assembly: HostingStartup]` without it being named anywhere — so the service can be
converted, built, inventoried and committed before any host exists.

```bash
dotnet restore
dotnet format analyzers Shop.slnx --diagnostics MOD0015 --severity info --include src/Ordering.API/
```

- **`--severity info`**, or the rule is skipped: a suggestion is below the default `warn`.
- **The solution, not the project.** With the solution loaded, a project only this service
  references (its Application, its Infrastructure) counts as its own code and its registrations
  move; a library other programs share does not. Given the project alone, only its own source
  counts and less moves.
- **Restore first.** A project with a stale `obj` loads without the analyzer and is skipped without
  a word; `dotnet restore --force` when a project you expected is untouched.
- From Git Bash on Windows pass the path in Windows form. Given `/c/...`, `dotnet format` matches
  no document and changes nothing, without an error.
- It rewrites each converted project file (a redundant `<Compile Include="Module.cs" />`, a
  byte-order mark, blank lines). Nothing in it matters to the build; `git checkout -- '*.csproj'`
  after the fix keeps the diff to the code — commit the `PackageReference` first.

**What moves:** registrations whose every call is the project's own or names its own types;
mappings of its own endpoints, into `UseEndpoints`; the locals only they use; an `if` without `else`
on the environment or configuration around them; the own calls out of a chain on the service
collection.

**What stays in `Program.cs`, on purpose:** the framework's and shared libraries' registrations,
and everything that shapes the whole application even when its options name the service's types —
authentication, authorization, Identity, MVC, CORS, OpenAPI, the message bus, schedulers, service
defaults; middleware; migrations and seeding; `MapControllers`, `MapRazorComponents`, fallbacks,
health checks; anything order-sensitive (`Replace`, `Remove…`, a registration of someone else's
contract after a statement that stays); anything in a loop, an `if`/`else` or `#if`. That remainder
is the list to reconcile with the host in Phase 1 — read it as the fix's answer to "what of this
service belongs to the application", not as work it failed to do.

When endpoints move and the program is left with no routing of its own, the fix adds
`app.UseModuleRouting()` after `Build()`. Keep it rather than replacing it with `UseRouting()`:
`WebApplication` adds authentication, authorization and antiforgery by itself *before* an explicit
`UseRouting()`, where no endpoint is chosen yet, and every endpoint that requires authorization then
answers 500. `UseModuleRouting()` adds routing and then those three, as `WebApplication` would have.

Then read what moved, with one thing in mind: **a module's `ConfigureServices` runs before every
statement of `Program.cs`**. The fix holds back what it can see depends on order. What it cannot
see is a contract of the project's own that the framework or a shared library also registers, and a
`TryAdd` that used to lose and now wins. A hosted service that moved starts before the port opens.
Compare the inventory from before (`dotnet modules inventory … --compare before.json`,
[verify.md](verify.md)) — it shows which registration wins and the order hosted services start in —
and run the service once.

When the service joins the host, the rest is a project-file change: `Microsoft.NET.Sdk.Web` becomes
`Microsoft.NET.Sdk`, `OutputType` goes, `Program.cs` is deleted after the host has taken what it
needs of it once, and the host references the project. Running the fix again is safe; it adds to
the module the project already has. Once done, `dotnet_diagnostic.MOD0015.severity = none` in the
project's `.editorconfig` silences a diagnostic that stays because a shared project's methods cannot
be read from a build.

**When it moves nothing.** A layered service whose `Program.cs` is one chain of the solution's own
methods — `AddApplicationServices().AddInfrastructureServices().AddApiServices()`, then
`app.UseApi()` — may have nothing the fix can take: the methods mix the service's registrations
with the application's. `dotnet format` then says the fix "did not return a Fix All action", once
per project. That is not an error. The work is to split those methods by hand — the service's own
registrations stay in the method and the method moves; authentication, the bus, OpenAPI go up to
the program — and it is real work: tens of files on a solution of nine such services. A program
that only calls `AddControllers` and `AddOpenApi` gets no diagnostic at all; write its module by
hand, empty if need be.

**The package reference is yours to add**, before the fix: `dotnet format` does not. Under central
package management that is a `PackageVersion` in `Directory.Packages.props` and a reference without
a version. If you script it, do not insert "after the last `<PackageReference`": one that spans
lines, with a `PrivateAssets` child, takes the new element inside itself (MSB4066), restore fails
for the whole solution, and unrelated projects report CS0246.

Things that keep their meaning only where they are written. The fix holds back the ones it
recognises; check them all the same, and apply the same list to anything moved by hand:

- **A registration that overrides another** by coming last: `AddTransient<IProfileService,
  ProfileService>()` after `AddIdentityServer().AddAspNetIdentity<…>()`. In the module it comes
  first and loses. Keep it after the call it overrides.
- **A registration that needs an earlier one**: a call that decorates what `AddIdentity`
  registered throws when it runs before it; a method that asks "is a health checker registered
  already?" quietly takes the other branch and four background services are never added.
- **A hosted service after the bus it uses.** Hosted services start in registration order. The
  order of log lines is not evidence — a bus logs from another thread; the inventory's start order
  is.
- **`Assembly.GetExecutingAssembly()`** in a statement that changes assembly: a consumer scan
  handed the wrong assembly builds, starts, and consumes nothing. Write
  `typeof(SomethingThere).Assembly`.
- **A local** every statement that uses it must stay with (CS0103 otherwise), and one that moved
  without its users is dead code.
- **An early `return` or `else`** in a method being split: `if (IsBuild()) { …; return; }` at the
  top guarded everything below it, and statements lifted out now run unconditionally.
- **A type named `Module` or `ModuleBase`** in the service's namespace (CS0115).

What usually has to be finished by hand, and is worth doing right after the fix rather than at the
merge:

- **A chain that starts with a shared library's call** stays whole even though the rest names the
  service's types: `builder.AddRabbitMqEventBus("eventbus").AddSubscription<OrderPaid, OrderPaidHandler>()`.
  Leave the first call in the program and write the subscriptions in the module.
- **An `if … else`** around registrations (one AI provider or the other) blocks them. Decide by
  hand; the condition usually reads configuration and moves as it is.
- **A `private` helper** a moved statement calls has to become `internal`.
- **Uniformity.** After the pass every service should look alike: a thin `Program.cs` of
  application-wide calls and a `Module.cs`. One that kept its wiring in `Program.cs` was skipped or
  held back by something — find out which before going on.

Do not copy what stayed into each module to make the module "complete". `AddDefaultAuthentication`,
`AddProblemDetails`, API versioning, OpenAPI and service defaults belong to the program now and to
the host later, once. A module that needs a marker to make such a call idempotent is telling you
the call is in the wrong place.

It is not reported for a worker on `Host.CreateApplicationBuilder` (a hosting startup runs only
under the web host), for a project on .NET 7 or older, or in a host that already composes modules.

## The builder is the module's view of the host

`ConfigureServices` takes an `IHostApplicationBuilder`, the same type `Program.cs` had, so the
service's registrations move over unchanged. That matters most for what is written as an extension of
the builder rather than of `IServiceCollection` — every Aspire client integration, and most component
packages. Those are not decoration over `AddDbContext`: they resolve `ConnectionStrings:<name>` out of
configuration, register a health check and instrument the calls. Registering the client by hand keeps
none of it.

```csharp
protected override void ConfigureServices(IHostApplicationBuilder builder)
{
    builder.AddNpgsqlDataSource("orders");
    builder.AddRedisClient("cache");
    builder.Services.AddScoped<IOrders, Orders>();
}
```

It is the module's view of the host rather than the host: the configuration is readable and closed
to new sources — `ConfigureAppConfiguration` is where those go, and adding one here throws rather
than disappearing — and the container is not a module's choice. `builder.Environment` is the web
host's environment; cast it to `IWebHostEnvironment` for `WebRootPath`.

Before 0.2 a module had a second overload, `ConfigureServices(WebHostBuilderContext, IServiceCollection)`,
the shape of the classic `Startup`. It still runs, after this one, and overriding it is a CS0672
warning whose code fix moves the body here (`services` becomes `builder.Services`, `context.Configuration`
becomes `builder.Configuration`, `context.HostingEnvironment` becomes `builder.Environment`). Splitting
registrations across both — the database in one, everything else in the other — was what most
migrated modules ended up doing, for no reason but that there were two.

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

## What a service assumed about being the process

None of these is a collision between two modules, so neither the startup checks nor
`dotnet modules topology` report them. Each was true while the service was the entry point and
stops being true, silently, in a host. Grep for them when a service is converted:

- **`Assembly.GetEntryAssembly()`, `typeof(Program)`.** The entry assembly is the host's now. A
  type lookup by name over it returns null — an event log that deserializes by event type name
  throws on the first event — and an assembly scan finds the host's handlers instead of the
  module's. Name a type of the module (`typeof(Module).Assembly`).
- **`ContentRootPath`, `WebRootPath`.** The host's. Files the service read from its own folder
  (seed data, pictures) are copied to the output and read from the module assembly's directory.
- **`appsettings*.json` and `launchSettings.json` of the service.** Dead once it is a library.
  Merge the keys into the host's, which is where the configuration-key collisions of Phase 0 get
  their answers.
- **Middleware the service added for itself.** In a module's `Configure` it runs for every request
  of the process, after the host's routing: `UseStatusCodePages()` of an API starts rewriting the
  storefront's pages. Confine it — `app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), …)`.
  Or it does not run at all: when the host maps an endpoint of its own, `WebApplication`'s endpoint
  middleware sits in the host's pipeline and executes the module's endpoints before the module's
  middleware is reached. Send a request and look for the middleware's effect; where it must wrap
  the module's endpoints and nothing else, an endpoint filter on the module's group says so
  directly.
  And middleware that had to run *before* routing no longer does: a cookie policy that set
  `SameSite` for the sign-in cookie stops covering it, and login loops over plain HTTP. Say it in
  the options of the thing itself instead.
- **`RequireAuthorization()` with no arguments.** It means the host's default scheme. An API that
  expected a bearer token, inside a host whose default is a cookie, answers a mobile client with a
  redirect to the login page instead of 401. Give the module's endpoints a named policy that names
  its scheme, gRPC services included.
- **A dictionary keyed by a short type name.** An event bus that maps `nameof(TEvent)` to one
  handler type worked while each service had its own copy of `OrderPaidIntegrationEvent`. With four
  assemblies declaring it, the last subscription wins and the other three handlers never run.
- **`wwwroot`.** Two projects with a `wwwroot/css/site.css` fail the host's build as soon as it
  references both. Set `StaticWebAssetBasePath` (`_content/<AssemblyName>`) in the module and fix
  the links in its views.
- **Build-time tooling that needs an entry point** — OpenAPI document generation at build, for
  one. It goes, or moves to the host.

## When the project becomes a library

MOD0001, MOD0004 and MOD0005 judge a project by what it is: while a converted service is still an
executable it is a host, and they are silent. They arrive together at the step that drops
`OutputType` — a couple of hundred MOD0001 on a mid-sized solution is normal. Do not add a switch to
silence them in between; there is nothing to silence until then. Do it as a step of its own:

```bash
dotnet format analyzers src/Ordering.API/Ordering.API.csproj --diagnostics MOD0001 --severity info --no-restore
```

Then the second wave, which no rule reports and the first build after the fix does not always
show:

- **Anything that scans the assembly for public types.** FluentValidation's
  `AddValidatorsFromAssemblyContaining<T>()` skips internal validators unless told
  `includeInternalTypes: true` — requests then pass unvalidated and no unit test fails.
  MassTransit's `AddConsumers(assembly)` takes public consumers only, and the message is then
  simply never handled. The framework's own `AddValidation()` skips internal types; Carter modules
  and a document database's document types have to stay public. Check every scan — validators,
  handlers, mapping profiles, consumers — and keep what must stay public with
  `fusion_modules_allowed_public_types`, each with its reason.
- **Generated code.** Source-generated partials (a `JsonSerializerContext`) stay as the generator
  wants them; `Protobuf` items take `Access="Internal"`.
- **Tests.** `InternalsVisibleTo` for the test project, and for `DynamicProxyGenAssembly2` when it
  mocks internal types (NSubstitute, Moq). It does not let a public test class name an internal
  type in a public signature (CS0050, CS0051): a host that builds can sit beside dozens of errors
  in the test projects.
- **Code generated from a shared file.** A `.proto` compiled into the module and again into the
  host as a client gives two copies of the same namespace in one compilation.
- **Six `Program` classes.** Reference the module projects from the host — and from tests — with
  an alias until their programs are deleted; see [verify.md](verify.md).

## Visibility

Build. MOD0001 will report every public type. Apply the "make internal" fix — Fix All handles a
converted service in one pass.

Exempt by default, because the framework finds them by reflection: view components, tag helpers,
the client interface of a SignalR `Hub<TClient>`, and entity types configured by an
`IEntityTypeConfiguration<T>` in the same assembly.

Controllers, page models and hubs are not exempt. A public class cannot have a public constructor taking an internal
service, nor a public action returning an internal DTO — so a public controller drags its services
and its models public with it, and then whatever those name. MOD0001 reports it and its fix makes
it internal; let discovery see it, or MOD0011 reports every route answering 404 (its fix writes
this):

```csharp
services.AddControllers()
    .AddApplicationPart(typeof(Module).Assembly)
    .AllowInternalControllers();
```

The class becomes `internal`, the constructor stays public — MVC's activator reads
`GetConstructors()`, which is public-only — and from there the parameters and return types may be
internal, because C# bounds a member's effective accessibility by its containing type. One call
covers the whole application.

The other kinds the framework reaches each answer differently, and guessing costs
a day:

- **View components** — `.AllowInternalViewComponents()`, same call site. Invoke by name or through
  the generic overload; both resolve at run time and both work. The one form that does not is
  `<vc:…>`, which the Razor compiler binds and which needs a public type — finding none, it reports
  nothing and copies the element into the page as literal HTML.
- **Razor Pages** — MOD0001 reports a public `PageModel` like a controller, and its fix makes it
  internal; nothing else to call. Page discovery reads the attributes the Razor compiler emits
  rather than scanning types, and the generated page class is itself internal. Keep the
  constructor and handler methods public.
- **SignalR hubs** — MOD0001 reports a public hub too, and its fix makes it internal; nothing else to
  call, because `MapHub` names the type. Keep the constructor and hub methods public. The client
  interface of a `Hub<TClient>` is the exception and stays public, for SignalR's generated proxy
  (MOD0010).
- **Tag helpers** — keep them public. `@addTagHelper` binds in the compiler against Roslyn symbols
  and requires public, for the current assembly as much as for referenced ones. An internal tag
  helper produces no descriptor at all: the element renders as literal HTML, with no error, no
  warning and nothing at runtime. Add it to the list of things the route inventory cannot see.

A hub's client interface needs a second sentence for the opposite reason: MOD0001 leaves it alone,
but SignalR still has to reach it. The proxy for a `Hub<TClient>` is generated into a dynamic
assembly of its own, which cannot implement an `internal` interface declared in yours — and nothing
says so until a client connects and the process throws `TypeLoadException`. MOD0010 asks the
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
fusion_modules_allowed_public_types = OrderDto, PaymentEnvelope
```

Write down why. Nobody can tell a considered exception from an abandoned one.

## Views and static assets

A module ships its own `Areas/`, `Pages/` and `wwwroot/`. It adds itself as an application part
inside `ConfigureServices`; the host must not, and FusionModules turns off the SDK behaviour that
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
