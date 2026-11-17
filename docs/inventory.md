# `dotnet modules inventory`

What a migration to modules needs to know about a solution, as one JSON document: how it is
deployed, what every project is and depends on, and — for each service — what its host actually
composes at runtime: the DI registrations, hosted services, `DbContext`s and endpoints, each with
the assembly that owns it. Captured without running the solution's infrastructure, the way
`dotnet ef` and build-time OpenAPI generation capture a host.

It is the first step of the skill's assessment phase, and replaces the `assess.ps1` script that
used to look for route and configuration collisions with regular expressions.

```bash
dotnet tool install --global FusionModules.Tool
dotnet modules inventory path/to/Shop.slnx --output fusion-modules-inventory.json
```

Or as a local tool, pinned with the repository:

```bash
dotnet new tool-manifest
dotnet tool install FusionModules.Tool
dotnet modules inventory --output fusion-modules-inventory.json
```

To install from a local or private feed into a repository whose `nuget.config` has
`<packageSourceMapping>`, `dotnet tool install --add-source <feed>` does not work: the mapping sends
`FusionModules.Tool` to the mapped sources only, and the added one is not among them. Add the feed
to the `nuget.config` with a mapping for `FusionModules*` instead, and install without
`--add-source`:

```xml
<packageSources>
  <add key="fusion-modules" value="path/to/feed" />
</packageSources>
<packageSourceMapping>
  <packageSource key="fusion-modules">
    <package pattern="FusionModules*" />
  </packageSource>
  <!-- the repository's own mappings stay as they are -->
</packageSourceMapping>
```

The tool targets `net10.0` and rolls forward onto any later runtime. The applications it reads may
target `net8.0`, `net9.0` or `net10.0` and run in their own runtime, which must be installed, as it
would be to run them. An application on `net5.0` to `net7.0` (or `netcoreapp3.x`) is probed rolled
forward onto the newest runtime installed — see [the probe](#the-probe). A .NET Framework
application is static information only.

## Usage

```
dotnet modules inventory [path] [options]
```

`path` is a solution (`.sln`, `.slnx`, `.slnf`), a project, or a directory holding exactly one
solution — or, failing that, exactly one project. A directory with `Shop.sln` and `Shop.slnx` is
read as the `.slnx`. A directory with neither at its top — a repository root whose solution is
`src/Shop.sln` — is searched three levels down, leaving out `bin`, `obj`, `node_modules` and
dot-folders: exactly one solution there is the input (and its directory the root); several are
listed in the error. Defaults to the current directory.

| Option | |
|---|---|
| `-o`, `--output <file>` | Write the JSON there. Without it, the JSON goes to standard output and progress to standard error. |
| `--static` | Evaluate the projects and read the deployment files only: no restore, no build, no probe. Seconds, and nothing under `hosts[].runtime`. |
| `--timeout <seconds>` | How long one host may take to start under the probe. Default 60. |
| `-c`, `--configuration <c>` | Build configuration. Default `Debug`. |
| `-f`, `--framework <tfm>` | The target framework multi-targeted projects are evaluated, built and probed with. Default: the highest plain `netX.Y` one. |
| `--environment <name>` | `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` for every probed host. Default: what the deployment sets, else `Development`. |
| `--env NAME=VALUE` | An extra environment variable for every probed host, on top of the deployment's. Repeatable. `--env HOSTINGSTARTUPASSEMBLIES=A;B` probes a FusionModules host as a given topology. |
| `--env host:NAME=VALUE` | The same for one host, named by its service, project or assembly name: `--env auth:ConnectionStrings__common=…` gives the connection string to `auth` and to no host that would register more for having it. A prefix that names no host is part of the variable's name (`--env Logging:LogLevel:Default=Debug` is for every host). The AppHost is probed before the hosts are known, so for it a prefix is decided against the solution's projects: one that names no project is part of the variable's name (`--env Parameters:pg-password=…`). |
| `--launch-profile <name>` | The `launchSettings.json` profile a host without orchestration is probed with. Default: the first profile with `"commandName": "Project"`, as `dotnet run` picks. |
| `--compose-file <file>` | The compose files to read, instead of discovering them — and instead of an Aspire AppHost in the solution. Repeatable, in override order. |
| `--no-docker` | Read compose files directly rather than through `docker compose config`. |
| `--all-services` | List every DI registration. By default only those touching the solution's own assemblies are listed, and the rest are counted per assembly. |
| `--work-dir <dir>` | Where restore and build output go. Default: a directory per solution under the system temp directory, kept between runs so the next build is incremental. |
| `--parallel <n>` | Hosts probed at once. Default: half the processors, at most 4. |
| `-q`, `--quiet` | No progress. |
| `--compare <file>` | Compare the new document with one taken before, as [`compare`](#comparing-two-inventories) does: the report goes to standard output with `-o`, to standard error without. |

The exit code is 0 whenever a document was written, however many services failed to build or
start — those are in `errors` — and with `--compare`, 2 when the two differ. It is 1 when the input cannot be resolved or an argument cannot be
read (`--timeout abc`, `--parallel 0`): the message and a pointer to `--help` go to standard error.

## What it reads, in order

1. **The input.** For a solution, its projects by `dotnet sln list`; a solution filter (`.slnf`)
   is read as the JSON it is — `solution.path` relative to the filter, `projects` relative to the
   solution, either path separator. Then every project they reference, transitively, whether in
   the solution or not.
2. **Every project, by MSBuild evaluation** — `dotnet msbuild -getProperty -getItem`, which runs no
   targets: the same properties and items the build sees, conditions, imports, `Directory.*.props`
   and central package management included. A multi-targeted project is evaluated a second time
   for one target framework, because items conditioned on the framework only exist there.
3. **The deployment**, the first of these that exists:
   - the compose files `--compose-file` names, AppHost or not;
   - an **Aspire AppHost** in the solution (`IsAspireHost`, which `Aspire.AppHost.Sdk` sets, or a
     reference to `Aspire.Hosting.AppHost`) that is not a test project — unless it can be neither
     built nor read and there are compose files (step 5);
   - **compose files**: `compose.yaml`, `compose.yml`, `docker-compose.yaml` or `docker-compose.yml`
     with its `.override` file — what `docker compose up` reads by default — in the solution's
     directory, else in the directories two levels below it, else in the directories above it up
     to the repository root. Other compose files beside them are listed in `deployment.notes`, not
     merged. Compose files that tie not one service to a project of the solution (below) are not
     the deployment: a note says so, and the next item is;
   - the **executable projects that are not tests**, each taken to be a service of its own and run
     with its launch profile. A note says so when there are several: nothing in the solution says
     how they are deployed together.

   For a project input: an AppHost is read as above; a web, worker or console project is the one
   service; anything else is static information only.
4. **Restore** of the solution or project, into the work directory.
5. **The AppHost**, when there is one: built and probed (below) to read its
   `DistributedApplicationModel`. If that fails, an Aspire 8 or 9 AppHost is asked for its manifest
   (`--publisher manifest`). If that fails too — or the AppHost does not build at all, as an Aspire 8
   preview AppHost does not on SDK 10 (NETSDK1228) — the compose files are read instead when there
   are any, and otherwise the projects the AppHost references stand in for its services, without
   environment or references. Either way a line on standard error and a note in
   `deployment.notes` give the reason: the first build error's code and message, or the probe's
   and the manifest publisher's last lines.
6. **The hosts**: every deployment service backed by a web, worker or console project of the
   solution. All of them are built in one MSBuild invocation, then each is probed in its own
   process.

### Compose

With the docker CLI on the `PATH`, `docker compose -f … config --format json` normalises the files
exactly as `up` would: overrides, `.env`, interpolation, every shorthand. It needs no daemon.
Without it — or with `--no-docker` — the files are read here: services merged in file order,
environment lists and maps, `${VAR}`, `${VAR:-default}`, `${VAR-default}` from the shell and
`.env`, short and long port syntax, `depends_on`, `deploy.replicas`.

A service with a `build` is tied to the project that produces its image:

1. the assembly its `ENTRYPOINT` (or `CMD`) runs — `["dotnet", "Catalog.API.dll"]` is the project
   whose `AssemblyName` is `Catalog.API`;
2. else the project `RUN dotnet publish` or `dotnet build` names;
3. else the project in the Dockerfile's directory.

Test and library projects are never candidates. `build.mappedBy` says which.

A service without a `build` names an image built elsewhere — by CI, say. Its image is classified:
`postgres`, `sqlserver`, `redis`, `rabbitmq`, `kafka`, `mongodb` and so on (by the service's name
when the image is left empty, `image: ${MYSQL_IMAGE}` with nothing to fill it in), and a known one
is `infrastructure`. The image is read as words — the runs of letters and digits of its registry,
path and name, without tag and digest — and a known word must be one of them, or several in a row:
`mcr.microsoft.com/mssql/server` is `sqlserver`, `jaegertracing/all-in-one` is `jaeger`, but
`hotel-api` is no OpenTelemetry collector and `sequencer` no Seq. The same table of words reads an
Aspire resource's type and a client integration's configuration section, where words run together
(`AzureBlobStorageResource`, `Aspire:Confluent:Kafka`). Any other image is tied to a project by
name, ignoring case and `-`, `_`, `.`:

1. the service's name — `catalog-api` is `Catalog.Api`;
2. else the image's name without registry, user, tag and digest —
   `${REGISTRY}/catalog-worker-outbox:latest` is `Catalog.Worker.Outbox`;

against each executable project's name, its assembly name, and the assembly the `ENTRYPOINT` of a
`Dockerfile` beside it runs. `mappedBy` (`service-name` or `image-name`) and `evidence` on the
service say which matched. A name that matches several projects ties none, and a note says so; an
image that matches nothing is a `container`, listed in a note and not probed.

### Aspire

The AppHost is a host like any other: `DistributedApplication.CreateBuilder` wraps a
`HostApplicationBuilder`, so the probe sees it being built, and the `DistributedApplicationModel` is
in its container by then — which is how Aspire's own testing builder gets at it. The model is read
by reflection, by type and property name, so one probe reads Aspire 8, 9 and 13 AppHosts.

For each resource: its type; for a project, the project path (the `IProjectMetadata` annotation,
or any annotation with a `ProjectPath`);
image or Dockerfile; endpoints; replicas; relationships (`WithReference`, `WaitFor`, parents); and
its environment. The environment is computed by running the resource's environment callbacks
against a *publish-mode* execution context, so values come back as manifest expressions —
`{catalogdb.connectionString}`, `{basket-api.bindings.http.url}` — rather than as addresses of
containers that are not running. A resource another one's environment mentions is a reference of
type `environment`.

The project rebuilders and the shared build step Aspire 13 adds for its own development loop are
kept under `aspire` and left out of `deployment.services`.

## The probe

For every host, the tool builds the project and starts a second process **in the application's
own runtime**:

```
dotnet exec --depsfile App.deps.json --runtimeconfig App.runtimeconfig.json
            --additionalprobingpath <NuGet packages folder>
            <tool>/probe/FusionModules.Tool.Probe.dll --assembly App.dll ...
```

— the command `dotnet ef` uses. The probe targets `net8.0` and depends on nothing but the shared
framework, so the application's runtimeconfig decides the runtime, and the application's
`deps.json` decides every assembly version: the probe sees exactly what the application would.
An application older than the probe — `net5.0` to `net7.0`, `netcoreapp3.x` — is run with
`--roll-forward LatestMajor`: it and its shared frameworks roll forward onto the newest runtime
installed, whose `Microsoft.Extensions.Hosting` raises the events below. `runtime.runtime` names the
runtime it ran in and `runtime.rolledForwardFrom` the application's own framework. (A worker whose
`Microsoft.Extensions.Hosting` is an app-local package older than 6.0 raises none: `failed`, with a
hint.)
It loads the application's assembly, makes it the entry assembly (what `WebApplication` takes the
application name from, and with it user secrets and MVC's application part), and uses
`HostFactoryResolver` — the source file `dotnet ef` and `GetDocument.Insider` compile in — to run
the entry point on a thread of its own and hook the `Microsoft.Extensions.Hosting` diagnostic
events:

- **HostBuilding**: the probe adds a last `ConfigureServices`. It copies the service collection —
  every registration the application made, hosted services included — then makes the host
  harmless: every `IHostedService` except ASP.NET Core's own `GenericWebHostService` is removed,
  `HealthCheckService` is replaced by a no-op (resolving the real one builds every health check's
  dependencies — `MapHealthChecks` does, while the application maps its endpoints, and MassTransit
  creates its bus and checks its license right there), `IServer` is replaced by one that listens on
  nothing (as `GetDocument.Insider` does), and the host lifetime by one that ignores the console.
  None of this shows in what is reported: the copy was taken first.
- **HostBuilt**, seen on the entry point's own thread before `Build()` returns: the container
  exists. The probe notes what it needs to read the endpoints later should the application stop
  early — the `WebApplication` (reached through `GenericWebHostService`'s options, whose
  `ConfigureApplication` delegate closes over the `WebApplicationBuilder`, which keeps the
  application it built) and MVC's action descriptors. The entry point is let run on.
- **The server "starts"**: by then the request pipeline is built — every `Map…` and every
  module's `IStartupFilter` has run — so `EndpointDataSource` is complete. The probe reads it,
  resolves each `DbContextOptions<T>` for its provider and connection string, stops the
  application and writes its result.

The probe's working directory is a scratch directory under the work directory, one per service —
two services built from one project are probed at the same time — and the content root is the
project directory, as under `dotnet run`: `appsettings.json` and user secrets are found, and
whatever the application writes to a relative path does not land in the solution. The probe writes
its result beside the file the tool reads and renames it into place, so a probe killed while
writing leaves no half a result; one that cannot be read anyway is that host's error, not the run's.

### What it does when the application does not cooperate

- **Code between `Build()` and `Run()` throws** — a migration with no database, a seed with no
  broker, a bus without its license. The registrations were captured at HostBuilding and are
  reported; `status` is `built` and the exception is in `errors` with stage `startup`. The
  endpoints are what Program.cs mapped before it threw, read off the `WebApplication`'s own route
  builder (`endpointsReadFrom: application`), and what the `IStartupFilter`s map — FusionModules'
  modules map in theirs, which only run when the pipeline is built. The probe runs the filter chain
  itself, on a throwaway `ApplicationBuilder` with `UseRouting()` innermost in place of the
  application's own configuration, and reads what their `UseEndpoints` mapped
  (`application+startup-filters`). Nothing is built and no middleware runs; a filter that throws
  (a module's ledger checks, say) is a `startup-filter` warning in `errors`, the rest of the chain
  still runs, and a module that threw before mapping is asked to map anyway. Controllers and
  Razor pages the application never got as far as mapping are listed from MVC's action
  descriptors (`action-descriptors`): attribute routes as written, conventional ones without a
  route.
- **The pipeline fails to build** — a middleware whose constructor needs a connection. `Run()`
  disposes the host on the way out; the endpoints, the pipeline's own by then, are read while it is
  being disposed: `Host` disposes the content root's file provider before the service provider,
  and the probe's provider calls back. The same goes for an application that disposes its
  `WebApplication` itself (`await using`) after a failure.
- **It waits instead of throwing** — a connection retry policy. The probe gives up after
  `--timeout`: `status` is `timeout`, the registrations are there, and the endpoints mapped so far.
- Endpoints that could not be read at all are `null`, not `[]`, with an `endpoints` entry in
  `errors` saying why. An old-style `Startup` class maps in `Configure`, while the pipeline is
  built: when `Configure` itself throws — a `Database.Migrate()` at its top, with a `DbContext`
  parameter — it has mapped nothing yet, and there is nothing to read but MVC's controllers.
- **It fails at registration** for want of a connection string. Aspire's client integrations do —
  `ConnectionString is missing. It should be provided in 'ConnectionStrings:orders'…`. The tool
  reads the name and the integration off the message, supplies a fake connection string of the
  right shape, and probes again, up to three times. The names it invented are in
  `hosts[].fakedEnvironment`.
- **It is not a host.** A console application that does not use `Microsoft.Extensions.Hosting`
  would simply be run; it is not probed (`status: skipped`). Nor is a .NET Framework application,
  which no .NET runtime loads.

One service failing never fails the inventory: its build or probe error is recorded and the run
goes on.

`errors[].detail` is the throw site, then the application's own frames — the first is where its
code called into whatever threw — then the inner exceptions. `errors[].hint` says what probably
happened when the exception says it: infrastructure not running after `Build()` (start it, or pass
`--environment`/`--env` so that the code takes another path; it also says when the address was
the tool's fake one); a relative path resolved in the probe's scratch working directory;
MassTransit's missing license (`--env MT_LICENSE=…`). The progress line of a host that did not
start gives its first error and the hint. When several hosts stop over the database schema at
once — a table missing, or already there — while probed in parallel, a note says that hosts
sharing a database race on their startup migrations, and to run again with `--parallel 1`.

The probe writes its standard output and error as UTF-8, whatever the console's code page, so
that a localized message the application writes reaches `errors` as written.

### The configuration it fakes

The probed host gets the deployment's own environment — compose `environment` values as written;
for Aspire, the resource's environment with every expression replaced; for a project run on its
own, its launch profile's `environmentVariables` — plus `ASPNETCORE_ENVIRONMENT` and
`DOTNET_ENVIRONMENT` (default `Development`) and `--env`. Expressions become values that parse and
point at nothing — never at a service of this machine, where a real database may well listen on
its default port. Every fake address is `127.0.0.1` on one port nothing listens on: port 1, else 9,
else one the system has just handed out — checked once per run with a connection attempt that is
given 250 ms. A listener on loopback accepts within a millisecond or two, so an attempt not
answered by then finds none: refused, or — on Windows, which retries a refused connection to
loopback for about two seconds before it reports the refusal — still being retried. A client that
connects after all is refused (on Windows, after those two seconds), and no name is resolved: a TUN
proxy's fake-IP DNS answers any name, `.invalid` included, and would take the connection. The
probe is told the address (`--fake-address`), so a hint can say when a failure was against it.

The probe and the manifest publisher run the application's code, so they do not inherit the
application configuration of the shell the tool runs in — a developer's real connection string
would otherwise beat the fake one. Left out: every variable whose name is a configuration key (it
holds `__` or `:` — `ConnectionStrings__db`, `Logging__LogLevel__Default`), `ASPNETCORE_*`,
`HOSTINGSTARTUPASSEMBLIES`, `DOTNET_*` except the runtime's own (`DOTNET_ROOT*`,
`DOTNET_HOST_PATH`, `DOTNET_CLI_*`, `DOTNET_NOLOGO`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`,
`DOTNET_MULTILEVEL_LOOKUP`, `DOTNET_ROLL_FORWARD*`, `DOTNET_GC*` — so `DOTNET_STARTUP_HOOKS` is
gone too), cloud SDK credentials (`AZURE_*`, `AWS_*`, `GOOGLE_*`, `GCLOUD_*`) and anything named
like a secret (`GITHUB_TOKEN`). Everything else — `PATH`, `TEMP`, `NUGET_PACKAGES` — is inherited.
What the deployment, the fakes and `--env` set is given on top, whatever its name. `dotnet build`,
`msbuild` and `docker` inherit the shell's environment, less the MSBuild variables `dotnet run`
and `dotnet test` set for their own child.

| Expression | Becomes |
|---|---|
| `{db.connectionString}` | The resource's own connection string expression, faked the same way, recursively: `kurrentdb://{kurrentdb.bindings.http.host}:{kurrentdb.bindings.http.port}?tls=false` becomes `kurrentdb://127.0.0.1:1?tls=false`, `{postgres.connectionString};Database=db` the server's with the database. Only when the model has no expression, or one with nothing to fake it from (Bicep `outputs`), a guess by the resource's type: `Host=127.0.0.1;Port=1;Database=db;…;Timeout=2` for PostgreSQL, `Server=127.0.0.1,1;…` for SQL Server, `127.0.0.1:1,…` for Redis, `amqp://guest:<secret>@127.0.0.1:1` for RabbitMQ, `kurrentdb://…@127.0.0.1:1?tls=false` for KurrentDB, explicit endpoints with the emulator's account key for Azure Storage (not `UseDevelopmentStorage=true`, which is Azurite on this machine), and so on |
| `{api.bindings.http.url}` | `http://127.0.0.1:1` |
| the resource's own `{self.bindings.http.url}` | `http://localhost:<port>`, one port per endpoint: `ASPNETCORE_URLS` never lists one port twice. Nothing listens there either: the probe's server binds nothing |
| `.host`, `.port`, `.targetPort`, `.hostAndPort`, `.scheme` | `127.0.0.1`, the refusing port, both, the scheme |
| `{param.value}` and anything else | one fake secret — base64 of 48 bytes, letters and digits only — which is also every password in the fake connection strings, so `POSTGRES_PASSWORD` and the connection string agree, and a parameter used as a signing key is long enough |

The host never connects to any of it: the probe resolves no client, and the hosted services that
would have are gone. The fakes exist so that registration-time validation passes.

Configuration the deployment itself sets — compose `environment` values, launch profiles,
`appsettings*.json` — is used as written. A `Development` configuration pointing at `localhost`
is the application's own, and its code before `Run()` will connect there; pass
`--environment`/`--env` to point it elsewhere.

### Secrets

One rule says what a secret is: a **name** whose last segment ends in `password`, `passwd`, `pwd`,
`secret`, `token`, `key`, `credential(s)`, `passphrase` or `signature` — `Password`,
`ClientSecret`, `ApiKey`, `Jwt:SigningKey`, `RABBITMQ_PASSWORD`, `SaslPassword`,
`SharedAccessSignature` (`SharedAccessKeyName` is not one). A value of `true` or `false` is a
switch and is never masked. Masked values become `***`:

- **Configuration** — `hosts[].configuration`, `hosts[].environment`,
  `deployment.services[].environment` and `connectionString`, `aspire.resources[].environment` and
  `connectionString`, `collisions.configurationKeys`, and the probe's
  `dbContexts[].connectionString`: a value under a secret's name whole. In any other value, every
  `key=value` pair of a connection string whose key is a secret's name — `Password=`, `Pwd=`,
  `Key=` (Aspire's OpenAI, Qdrant, Milvus), `ApiKey=`, `Token=`, `AccountKey=`, `SharedAccessKey=`,
  `SharedAccessSignature=` — up to the next `;` (or `,` in a Redis connection string, which has no
  `;`); a URI's `user:password@`; and a URI query parameter under a secret's name, or `sig`.
- **Free text** — `deployment.services[].command`, `aspire.resources[].command`, `deployment.notes`,
  `hosts[].buildError`, the `message`, `detail` and `hint` of `hosts[].runtime.errors` (the
  application's own output among them) and of `aspire.resources[].errors`, and `errors[].message`:
  `NAME=value` with a secret's name, up to the next space; a flag with a secret's name, or
  `--requirepass`, `--masterauth`, `--pass` or `--auth`, and the value after it
  (`redis-server --requirepass ***`); URI credentials as above; and every occurrence of a secret
  value found anywhere in the configuration above or among the shell's variables with a secret's
  name, six characters or longer — so a password interpolated into a command, or quoted in an
  exception message, goes whatever surrounds it.

Collisions are found on the real values first: two different passwords are still a collision,
shown masked. What the masking cannot know is a secret under an ordinary name, in a format it does
not read: review the document before sharing it.

### Limits

- **It runs the application's code** up to `Run()`, with fake configuration, exactly as
  `dotnet ef migrations add` does. Code that does something irreversible before `Run()` on a
  development configuration will do it here.
- **The manifest fallback runs an AppHost's Program to its end**, in publish mode
  (`--publisher manifest`), when the probe could not read its model: whatever the AppHost does
  after `Run()` — and whatever its own code does in publish mode — runs too.
- **The build runs the projects' own targets.** Output goes to the work directory (MSBuild's
  artifacts layout, as global properties) and build-time OpenAPI document generation is switched
  off, but a custom target that writes into the source tree still does.
- **Middleware order is not captured**: a built pipeline is a chain of delegates, not a list.
- **Endpoints of a host stopped by its own startup code** are those mapped before it stopped: a
  `WebApplication`'s up to the failure, its startup filters' (modules'), MVC's controllers and
  pages always; an old-style `Startup` host's none (`null`).
- **Startup filters run** when a host stopped before its pipeline was built, to collect what they
  map: a filter that does more than configure the pipeline does that too, as it would at startup.
- **One topology per run** for a FusionModules host: the one its environment selects. Run again
  with `--env HOSTINGSTARTUPASSEMBLIES=…` or `--launch-profile` for another.
- **Aspire resources added by lifecycle hooks at start** (the dashboard, for one) are not in a
  model that was only built.

## The document

Paths are relative to `root`, with forward slashes, unless they lie outside it. Properties that
would be empty are left out.

```jsonc
{
  "schemaVersion": 1,
  "tool": "FusionModules.Tool 0.3.0",
  "generatedAtUtc": "2026-11-07T15:02:46Z",
  "durationMs": 11498,
  "root": "D:\\src\\eShop",
  "input": { "path": "D:\\src\\eShop\\eShop.slnx", "kind": "solution", "solution": "eShop.slnx" },

  "deployment": {
    "kind": "aspire",                     // aspire | compose | executables | project | none
    "sources": ["src/eShop.AppHost/eShop.AppHost.csproj"],
    "readBy": "probe",                    // probe | manifest | project-references | docker-compose-config | yaml | msbuild
    "notes": ["10 resource(s) the AppHost adds for its own development loop are left out …"],
    "services": [
      {
        "name": "catalog-api",
        "kind": "project",                // project | container | executable | parameter | infrastructure | other
        "type": "ProjectResource",        // Aspire resource type, image class (postgres…) or project kind (web…)
        "project": "src/Catalog.API/Catalog.API.csproj",
        "replicas": 2,
        "environment": { "ConnectionStrings__catalogdb": "{catalogdb.connectionString}" },
        "references": [
          { "name": "catalogdb", "type": "Reference" },   // Reference | WaitFor | Parent | depends_on | environment
          { "name": "eventbus", "type": "WaitFor" }
        ],
        "ports": [ { "name": "http", "scheme": "http", "protocol": "http", "port": 5222, "external": false } ]
      },
      { "name": "catalogdb", "kind": "infrastructure", "type": "PostgresDatabaseResource", "parent": "postgres",
        "connectionString": "{postgres.connectionString};Database=catalogdb" },
      // compose: "image", "build": { "context", "dockerfile", "mappedBy": "entrypoint", "evidence": "Catalog.API.dll" };
      //          without a build: "mappedBy": "service-name" | "image-name", "evidence": "catalog-api matches project Catalog.Api"
      // executables: "launchProfile", and the profile's environment and applicationUrl ports
    ]
  },

  "projects": [
    {
      "path": "src/Catalog.API/Catalog.API.csproj",
      "name": "Catalog.API",
      "assemblyName": "Catalog.API",
      "sdk": "Microsoft.NET.Sdk.Web",
      "kind": "web",                      // aspire-host | web | worker | console | client | test | library
      "outputType": "Exe",
      "targetFrameworks": ["net10.0"],
      "isTestProject": false,
      "inSolution": true,
      "projectReferences": ["src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj", "…"],
      "transitiveProjectReferences": ["…"],
      "packageReferences": [
        { "id": "Aspire.Npgsql.EntityFrameworkCore.PostgreSQL", "requested": "13.6.0", "resolved": "13.6.0" },
        { "id": "Microsoft.NET.ILLink.Tasks", "requested": "10.0.3", "resolved": "10.0.3", "implicit": true }
      ],
      "frameworkReferences": ["Microsoft.AspNetCore.App"],
      "properties": { "UserSecretsId": "…" },
      "error": null                       // the evaluation error, for a project MSBuild could not evaluate
    }
  ],

  "hosts": [
    {
      "service": "catalog-api",
      "project": "src/Catalog.API/Catalog.API.csproj",
      "targetFramework": "net10.0",
      "environment": { "ConnectionStrings__catalogdb": "Host=127.0.0.1;Port=1;Database=catalogdb;…;Password=***" },
      "fakedEnvironment": ["ConnectionStrings__catalogdb"],
      "launchProfiles": ["http"],
      "configuration": { "appsettings.json": { "EventBus:SubscriptionClientName": "Catalog" } },
      "buildError": null,
      "runtime": {
        "mode": "service",
        "status": "started",              // started | built | timeout | failed | skipped
        "runtime": ".NET 10.0.14",        // the runtime the probe ran in
        "rolledForwardFrom": null,        // "net6.0": an older application, run rolled forward onto runtime
        "durationMs": 524,
        "environmentName": "Development",
        "hostingStartupAssemblies": ["CatalogModule"],
        "applicationAssemblies": ["Catalog.API", "EventBus", "…"],  // deps.json "project" libraries
        "totalServices": 378,
        "services": [
          { "serviceType": "eShop.IntegrationEventLogEF.Services.IIntegrationEventLogService", "lifetime": "Scoped",
            "implementation": "type",     // type | instance | factory
            "implementationType": "eShop.IntegrationEventLogEF.Services.IntegrationEventLogService<eShop.Catalog.API.Infrastructure.CatalogContext>",
            "assembly": "IntegrationEventLogEF", "serviceAssembly": "IntegrationEventLogEF",
            "module": "Catalog.API" },   // the module whose ConfigureServices made it; null for the program's
          { "serviceType": "…", "implementation": "factory", "factory": "Microsoft.Extensions.DependencyInjection.Foo.<AddFoo>b__0_0", "key": "orders" },
          { "serviceType": "Duende.IdentityServer.Services.IProfileService", "implementationType": "…ProfileService", "wins": false },
          { "serviceType": "Duende.IdentityServer.Services.IProfileService", "implementationType": "…ProfileService<…ApplicationUser>", "wins": true }
        ],
        "frameworkServices": { "Microsoft.Extensions.Options": 42 },
        "modules": [ { "type": "Catalog.API.Module", "assembly": "Catalog.API" } ],   // FusionModules modules the host activated
        "hostedServices": [ { "type": "eShop.OrderProcessor.Services.GracePeriodManagerService", "assembly": "OrderProcessor", "application": true } ],
        "dbContexts": [
          { "type": "eShop.Catalog.API.Infrastructure.CatalogContext", "assembly": "Catalog.API", "lifetime": "Scoped", "pooled": true,
            "provider": "Npgsql.EntityFrameworkCore.PostgreSQL",
            "connectionString": "Host=127.0.0.1;Port=1;Database=catalogdb;Username=postgres;Password=***",
            "migrationsAssembly": null, "migrationsHistoryTable": null,
            "model": {                       // null: not read — "modelError" says why
              "lines": [ "Annotations:", "  Relational:MaxIdentifierLength: 63" ],
              "entities": [
                { "name": "eShop.Catalog.API.Model.CatalogBrand",
                  "lines": [ "EntityType: CatalogBrand", "  Properties:", "    Id (int) Required PK AfterSave:Throw ValueGenerated.OnAdd",
                             "    Brand (string) Required MaxLength(100)", "  Keys:", "    Id PK",
                             "  Annotations:", "    Relational:TableName: CatalogBrand" ] }
              ] } }
        ],
        "endpointsReadFrom": "pipeline",  // pipeline | application | startup-filters | action-descriptors, joined with +
        "staticAssetEndpoints": null,     // 2096: what MapStaticAssets mapped, one static-assets entry below
        "endpoints": [                    // null: not read (not a web host, or it stopped first — errors say why)
          { "route": "/api/catalog/items/{id:int}", "methods": ["GET"], "kind": "minimal",   // minimal | controller | page | component | hub | grpc | static-assets | other
            "handler": "eShop.Catalog.API.CatalogApi.GetItemById", "assembly": "Catalog.API",
            "name": "GetItemById", "authorization": ["policy=Admin"], "allowAnonymous": false },
          { "route": "/Account/Login/{id?}", "template": "{controller=Home}/{action=Index}/{id?}", "kind": "controller",
            "handler": "IdentityServerHost.Quickstart.UI.AccountController.Login", "assembly": "Identity.API" }
        ],
        "errors": [ { "stage": "startup", "type": "Npgsql.NpgsqlException", "message": "Failed to connect to 127.0.0.1:5432",
                      "detail": "thrown at Npgsql.…\ncalled from the application's code:\n  at Catalog.API.Extensions.MigrateDbContext in …:line 40\n…\ncaused by System.Net.Sockets.SocketException: …",
                      "hint": "The application's code after Build() failed (in Catalog.API.Extensions.MigrateDbContext): PostgreSQL is not running or not reachable. Start it, or pass --environment/--env …" } ]
      }
    }
  ],

  "aspire": { "version": "13.6.0", "resources": [ /* every resource, with its annotation type names */ ] },

  "collisions": {
    "services": [
      { "serviceType": "eShop.IntegrationEventLogEF.Services.IIntegrationEventLogService", "byModules": true,
        "sites": [ { "service": "catalog-api", "module": "Catalog.API", "implementations": ["…<CatalogContext> (IntegrationEventLogEF)"] },
                   { "service": "ordering-api", "module": "Ordering.API", "implementations": ["…<OrderingContext> (IntegrationEventLogEF)"] } ] }
    ],
    "routes": [
      { "route": "/health", "sharedHandler": true,
        "sites": [ { "service": "catalog-api", "route": "/health", "handler": "…HealthCheckMiddleware.InvokeAsync", "assembly": "…" } ] }
    ],
    "endpointNames": [ { "name": "GetItemById", "sites": [ { "service": "catalog-api", "where": "GET /api/catalog/items/{id:int}" } ] } ],
    "typeNames": [ { "name": "Basket.API.Grpc.BasketClient", "sites": [ { "service": "webapp", "where": "WebApp" } ] } ],
    "configurationKeys": [
      { "key": "Identity:Audience", "source": "appsettings.json",
        "values": [ { "service": "basket-api", "value": "basket" }, { "service": "ordering-api", "value": "orders" } ] }
    ],
    "dbContexts": [ { "type": "…", "sites": [ { "service": "…", "provider": "…", "connectionString": "…" } ] } ]
  },

  "errors": [ { "stage": "probe", "subject": "ordering.api", "message": "built: Microsoft.Data.SqlClient.SqlException: …" } ]
}
```

### Notes on the fields

- **`projects[].kind`** — `aspire-host`; `test` (`IsTestProject`, or a reference to a test
  framework package — checked first, because a functional test that starts Aspire resources is an
  AppHost too); `web` (Web SDK or `Microsoft.AspNetCore.App`); `worker` (Worker SDK or
  `Microsoft.Extensions.Hosting`); `client` (MAUI, WPF, Windows Forms, Blazor WebAssembly,
  `WinExe`, platform-specific target frameworks only); `console`; `library`.
- **`packageReferences[].requested`** is what the project asks for — `VersionOverride`, `Version`,
  or the central `PackageVersion`. **`resolved`** comes from the restore's `project.assets.json`.
- **`runtime.services`** lists a registration when its service type (or a generic argument of it),
  its implementation type, or the method its factory points at belongs to one of the
  application's assemblies — the `deps.json` project libraries. Everything else is counted in
  `frameworkServices`, by the assembly of the service type; `--all-services` lists it all.
  A factory's `assembly` is the assembly the delegate is declared in: for a lambda, the one that
  registered it. FusionModules' own wiring is neither: a module registering itself as an
  `IStartupFilter` is listed under `runtime.modules`, and FusionModules' assemblies are never
  application assemblies, even referenced as projects.
- **`runtime.services[].wins`** — set when one service type (and key) is registered more than once
  and is not one the framework resolves as a collection (options configuration, hosted services,
  startup filters, authorization handlers, MediatR handlers and behaviours): `true` on the
  registration a single resolution returns, the last, `false` on those it overrides. Registrations
  that touch no application type are listed too when they take part. A refactoring that changes
  registration order changes these.
- **`runtime.services[].module`** — the module whose `ConfigureServices` made the registration, as
  FusionModules records it for its startup checks: the module's assembly name. Null for what the
  program and the framework register, and in a host without modules.
- **`runtime.dbContexts[].lifetime`** — the context's own registration; `Pooled` for a pooled
  factory that registers no context.
- **`runtime.dbContexts[].model`** — the context's model in EF Core's own words: for every entity
  type its debug view (`ToDebugString(MetadataDebugStringOptions.LongDefault)`) line by line — table
  and schema, properties with their types, lengths, precision and converters, keys, indexes,
  navigations and foreign keys, owned types, annotations — followed by what the view leaves out: its
  query filters (`QueryFilter: o => …`) and its seed data, a `Seed:` line per row. `lines` is what
  the model says outside its entity types: a default schema, sequences. The design-time model is the
  one read, as `dotnet ef` reads it, and building it connects to nothing. The context is taken from
  the container (or from its `IDbContextFactory<T>`); one that cannot be created there — it needs a
  request, a tenant — has `model: null` and the exception in `modelError`, and everything else of it.
  Left out: the EF Core version, and the bookkeeping its conventions leave on a model.
- **`runtime.endpoints[].route`** is the route the endpoint answers. For a conventional MVC route
  that is the template with the action's values filled in — `/Account/Login/{id?}` — and the
  template is in `template`. Link-generation-only endpoints are left out. **`assembly`** is the
  owner: the controller's, the page's, the component's, the hub's or gRPC service's, the assembly
  declaring a minimal API's delegate; for anything else, the assembly of the request delegate's
  method — for a health check, the framework's. The endpoints `MapStaticAssets` maps — one per
  file and encoding, thousands in a web UI — are one entry of kind `static-assets`, without a
  route, and counted in `staticAssetEndpoints`.
- **`collisions`** — what the hosts would collide on in one process, by the rules of FusionModules'
  startup checks; [`topology`](#previewing-a-topology) reads the same over the hosts it names.
- **`collisions.services`** — a service type that two owners register with different sets of
  implementations, an implementation told apart with its assembly (a type name compiled into two
  services is two types). The owners are modules (`byModules: true`, each site a module, two in one
  host as well as in two): the startup check fails. Or they are programs (`byModules: false`): the
  composed host registers it once, from one program, and the difference is reconciled there. A host
  with no module yet counts as its future module. The framework's services — consumed as sequences
  — EF Core's `DbContextOptions` and FusionModules' own are left out.
- **`collisions.routes`** — one route across *services*, compared as the matcher compares routes:
  literals case-insensitively, parameters by their constraints and not their names (`{id}` and
  `{itemId}` are one route, `{id:int}` another), with overlapping HTTP methods (no methods means
  all). `sharedHandler` marks a route every site serves with the same handler, usually mapped by a
  shared library.
- **`collisions.endpointNames`** — one endpoint name on two endpoints: names are unique in a host,
  and generating a link by name throws.
- **`collisions.typeNames`** — one full service type name from two assemblies: named HTTP and gRPC
  clients and options key by the name and see one.
- **`collisions.dbContexts`** — one context type that two services point at different databases or
  providers.
- **`collisions.configurationKeys`** — the same key with different values in two services,
  compared per file name (`appsettings.json` with `appsettings.json`, `appsettings.Development.json`
  with its namesake), and in the environment the deployment sets (`source: environment`, variables
  read as keys: `Identity__Url` is `Identity:Url`; faked values and `ASPNETCORE_*`, `DOTNET_*`,
  `OTEL_*` left out). `"//"` properties, comments by convention, and `$schema` are not keys.

## Comparing two inventories

```bash
dotnet modules inventory Shop.slnx -o before.json
# … a step of the migration …
dotnet modules inventory Shop.slnx -o after.json --compare before.json
dotnet modules compare before.json after.json          # the same, from two files
```

Host by host, what each composes: its endpoints with their authorization, its registrations and
which of them wins, the framework's registrations per library, its hosted services and the order the
application's start in, its DbContexts with their databases and their models, its status and
errors. One line per thing, `-` for what is gone and `+` for what is new; a host is "the same" when
it has none. What a
conversion changes by design is not a difference: a lambda that moved from `Program` to `Module`
or into a method of the service's own (a lambda is compared by its assembly), keys made unique per run (Scrutor's `Decorate`), FusionModules'
own registrations. Modules that come or go, and errors that are gone, are notes. Exit code 0 the
same, 2 different, 1 when a file is not an inventory.

A context's model is compared entity type by entity type. One that came or went is a line; one
that changed has a line for each thing that did, under the headings it stands under in the debug
view, so that an annotation says whose it is:

```text
ordering-api: 3 differences
  - model Ordering.OrderingContext: Ordering.Order: EntityType / Properties / Total / Annotations / Precision: 18
  + model Ordering.OrderingContext: Ordering.Order: EntityType / Properties / Total / Annotations / Precision: 10
  - model Ordering.OrderingContext: Ordering.Order: EntityType / Seed: Id=1, Name="first"
```

That is the check for a refactoring of `OnModelCreating` — [MOD0016](rules/MOD0016.md)'s fix, which
moves entity configuration into `IEntityTypeConfiguration<T>` classes, or a hand-made one: the
model is the same when nothing is reported. A query filter is compared as its expression reads,
without the numbers the compiler gives closures, so one that moved to another class is the same
filter. A model that one of the two inventories does not have — taken by an earlier version of the
tool, or the context could not be created — is a note, `model of … not compared`, not a difference.

When services join one host, `--merged` compares the host with the services it took in:

```bash
dotnet modules compare before.json after.json --merged webapp catalog-api basket-api ordering-api
```

The host serves the union of their endpoints and starts their hosted services, each no more times
than one service did — a bus started by five modules runs five times — and has every registration
and DbContext one of them had, the programs' shared ones once. A context type that several of them
had — one composed from modules — has in the host every entity type any of them had in it, each as
it was; an entity type only the host has is a note.

## Previewing a topology

```bash
dotnet modules topology inventory.json                           # every host of the inventory
dotnet modules topology inventory.json catalog-api ordering-api  # these, as one host
dotnet modules topology inventory.json --pairs                   # which pairs merge cleanly
```

What a host composing these services would report, before anyone builds it: the
[`collisions`](#notes-on-the-fields) over the hosts named, in order of their consequences, each kind
with what to do about it by hand. Modules registering one service differently fail startup; one
endpoint name twice fails requests; programs registering one service differently are reconciled in
the host's program; one route in two services is logged at startup and answers 500; one type name
from two assemblies, one configuration key with two values and one DbContext on two databases
follow. Exit code 2 when the host would fail startup.

It reads what the services register and map when they run, not their source, so it sees what the
inventory sees and no more. Not in it: options (two `AddJwtBearer` with different audiences, a
global `AuthorizeFilter`), what factories and instances return, and the contracts the startup check
lets through — `[MultipleImplementations]` and `FusionModules:AllowedServiceCollisions` — which it
reports all the same. A service with no module yet counts as its future module: move its
`Program.cs` into one (MOD0015) and take the inventory again to tell its program's registrations
apart.

## The work directory

`%TEMP%/fusion-modules-inventory/<solution>-<hash>` by default: `artifacts/` (MSBuild's artifacts
layout — `bin/`, `obj/` and the restore's assets for every project), `probes/` for results in
flight, `cwd/<service>/` for the probed applications' working directories. It is kept, so the next run
builds incrementally; delete it to start clean, or pass `--work-dir`.
