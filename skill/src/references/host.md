# Phase 1 — The host

The host is the part that must stay ignorant. Everything else is recoverable; a host that knows
about a module has given up the property the whole approach exists for.

## Monolith → modules: do not create one

The existing application *is* the host. Touch two things:

```xml
<PackageReference Include="Modulith" Version="..." />
```

```csharp
app.UseRouting();   // only if it is not already there
```

That is the whole of Phase 1 in this direction. `Program.cs` keeps its services, its middleware
and its endpoints; modules will take them over one at a time, and the application works after
every step. Resist any urge to "clean up the host first" — an empty host at the end is the
result of the migration, not a precondition for it.

## Microservices → modules: a new, empty one

Do not promote one of the services. It carries references to everything it used to call, so
MOD0003 fires on day one and the noise hides real findings. A new project starts green.

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
```

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Modulith" Version="..." />
  </ItemGroup>
</Project>
```

Module references get added in Phase 2, one at a time, as each module appears.

## What belongs in the host

Almost nothing, and the test is: *would every topology want this?*

Genuinely host-level:

- `UseRouting()` — needed when the host maps no endpoints of its own. When it does map some,
  `WebApplication` adds routing anyway; when it does not and a module tries to map endpoints,
  startup throws with a message naming the call. Either way it is cheap to be explicit.
- the composition root for infrastructure the deployment owns rather than the application —
  the database connection string, logging sinks, the OpenTelemetry exporter
- middleware whose order is a property of the application rather than of a feature: exception
  handling, forwarded headers, HTTPS redirection

Not host-level, however tempting:

- authentication and authorization *schemes* used by only some modules
- CORS policies for specific endpoints
- anything registered because one module needs it

When something is needed by every topology but is clearly a feature rather than infrastructure,
make it a module and put it first in every `HOSTINGSTARTUPASSEMBLIES`. The host project can carry
`[assembly: HostingStartup(typeof(Module))]` itself; ASP.NET Core activates the entry assembly
first.

## Kestrel endpoints

Merging services merges their listeners, and that cannot be a per-module decision: a module gets a
service collection and a place in the pipeline, never a port. Take the endpoint list out of Phase 0
and settle it here, once.

The trap is protocols. On a TLS endpoint `Http1AndHttp2` really is the union, because ALPN picks
per connection. On a **plaintext** endpoint there is no ALPN, so `Http1AndHttp2` means HTTP/1.1,
and every gRPC client that `Http2` would have served gets `HTTP_1_1_REQUIRED` on its first call.
The union reads like the safe merge of two services' settings, and it is the one setting that
silently drops gRPC.

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:8080", "Protocols": "Http1AndHttp2" },
      "Grpc": { "Url": "http://*:8081", "Protocols": "Http2" }
    }
  }
}
```

Keep the gRPC port on `Http2` unless something genuinely calls it over HTTP/1.1 — JSON transcoding
on the same port is the usual reason, and which services relied on that is worth knowing before you
take it away.

## Cross-cutting concerns, reconciled once

Phase 0 produced a diff table of middleware order and cross-cutting configuration across the
services. Reconcile it now, before any module is converted, and write the decisions down. Doing
it later means re-testing every module that has already moved.

The order of the host pipeline applies to every module. Where two services disagreed, one of them
is going to change behaviour — decide which, deliberately.

## Launch profiles

Add one profile per planned topology, plus `NoModules`. They are the cheapest possible
documentation of what the deployment shapes are, and they keep the topologies exercised during
development rather than only in CI.

```json
{
  "profiles": {
    "NoModules":  { "commandName": "Project" },
    "Orders":     { "commandName": "Project",
                    "environmentVariables": { "HOSTINGSTARTUPASSEMBLIES": "Orders.Entities;OrdersModule" } },
    "AllModules": { "commandName": "Project",
                    "environmentVariables": { "HOSTINGSTARTUPASSEMBLIES": "Orders.Entities;Customers.Entities;OrdersModule" } }
  }
}
```

## Gate

`dotnet run` starts and returns 404 for everything (or, in the monolith direction, behaves
exactly as it did before). `dotnet build -warnaserror` is clean. Only then start Phase 2.
