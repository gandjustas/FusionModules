# Phase 5 — Deployment

One Dockerfile. One image. As many deployments as there are topologies, differing only by an
environment variable.

```yaml
services:
  monolith:
    image: app
    build: &build { context: ., dockerfile: Host/Dockerfile }
    environment:
      HOSTINGSTARTUPASSEMBLIES: Orders.Entities;Customers.Entities;CustomersModule;BillingModule

  customers:
    image: app
    build: *build
    environment:
      HOSTINGSTARTUPASSEMBLIES: Customers.Entities;CustomersModule
      Database__Migrate: "false"

  billing:
    image: app
    build: *build
    environment:
      # The composing module goes last: its configuration extends the two above.
      HOSTINGSTARTUPASSEMBLIES: Orders.Entities;Customers.Entities;BillingModule
      Database__Migrate: "false"
```

Write the ordering rule as a comment wherever a composing module appears. It is invisible
otherwise, and reordering the list looks harmless.

## What goes away

- per-service Dockerfiles
- the CI build matrix over services
- service discovery configuration for edges that are now in-process
- the service mesh routes, retries and timeouts for those same edges

Delete them in the same commit as the change that makes them redundant, not later. Stale
infrastructure that still works is the hardest kind to remove afterwards.

## What replaces "is the service up"

A topology that loads the wrong modules still starts and still passes a plain liveness probe. Give
readiness something to check:

```csharp
builder.Services.AddHealthChecks().AddCheck("modules", () =>
{
    var expected = builder.Configuration["Deployment:ExpectedModules"]?.Split(';') ?? [];
    var loaded = ModuleBase.GetLoadedModules(builder.Configuration)
        .Select(a => a.GetName().Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missing = expected.Where(m => !loaded.Contains(m)).ToArray();
    return missing.Length == 0
        ? HealthCheckResult.Healthy()
        : HealthCheckResult.Unhealthy($"modules not loaded: {string.Join(", ", missing)}");
});
```

Three lines of the host's own code, not package API. `ModuleBase` already fails startup when a
*named* module cannot be loaded; this catches the other direction — a deployment whose variable
was edited to something that loads fine but is not what this replica is for.

## The Dockerfile

Standard multi-stage. Two things worth doing:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against project files alone, so a code change does not invalidate the restore layer.
COPY --parents **/*.csproj Directory.*.props global.json ./
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore Host/Host.csproj

COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Host/Host.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Host.dll"]
```

**Not `PublishAot` or `PublishTrimmed`.** Modules are loaded by name at runtime, so the trimmer
cannot see that they are used and will remove them. If the user was counting on AOT, this is the
moment to say so rather than after the first trimmed image fails to start.

## Aspire

```csharp
builder.AddProject<Projects.Host>("orders")
    .WithEnvironment("HOSTINGSTARTUPASSEMBLIES", "Orders.Entities;OrdersModule");
```

The same project resource, added more than once with different environment variables. Aspire
models this well; it is the same image, and the resource graph says so.

## Gate

- Every topology in the compose file starts and answers its health probe.
- The route inventory of each topology matches what the corresponding service served before.
- No per-service Dockerfile or CI job remains for a service that is now a module.
