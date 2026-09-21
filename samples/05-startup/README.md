# 05 — Startup work

What to do with the few lines a service had between `Build()` and `RunAsync()` — the schema check,
the seed, the cache warm — once that service is a module and has no such place.

```bash
cd Host
dotnet run --launch-profile NoModules   # /
dotnet run --launch-profile Catalog     # /catalog, already seeded on the first request
```

## The pattern

The module declares the work; the host runs it.

[`Contracts/StartupTask.cs`](Contracts/StartupTask.cs) is the declaration, and it lives in a plain
library rather than in a module — which is what lets both sides reference it without either
depending on the other. [`Modules/Catalog/Module.cs`](Modules/Catalog/Module.cs) registers one;
[`Host/Program.cs`](Host/Program.cs) enumerates them between `Build()` and `RunAsync()`. The host
still knows nothing about modules: it enumerates a contract it owns, and who registered one is none
of its business.

Two properties come with that shape and are worth naming. **Ordering belongs to the host**, which
is the whole reason to prefer it — a gate that must precede every module's work cannot be expressed
from inside one module. And **failure is loud**: the host is `await`ing, so an exception stops
startup instead of disappearing into a background task.

## What about `AddHostedService`?

Closer than it looks, and the sample is deliberately accurate about why it is still not the answer.

A module's hosted services are registered *before* `GenericWebHostService`, because a hosting
startup configures the builder before the web host adds its own services. So a module's
`StartAsync` runs with the port still closed, and blocking work there delays the first request
rather than racing it.
[`Tests/StartupOrderTests.cs`](Tests/StartupOrderTests.cs) asserts that registration order rather
than asking you to believe it, because it is the opposite of what the shape suggests.

What it does not give you is ordering between modules — hosted services start in registration
order, which is `HOSTINGSTARTUPASSEMBLIES` order. And it is registration order rather than a
contract; it has moved once already, between hosting models.
[`WarmCache`](Modules/Catalog/Catalog.cs) shows the contractual version,
`IHostedLifecycleService.StartingAsync`, which runs before any hosted service by specification.
Both trip MOD0008, correctly, and both suppressions in this sample say which decision they record.

The asserted sequence is the summary:

```
task:catalog-seed  →  lifecycle:starting  →  worker:start
```

## Testing a topology whose modules have workers

A module's hosted services really run inside `WebApplicationFactory`. A worker that opens a
database connection in `StartAsync` therefore fails every topology test on a machine with no
database — for a reason that has nothing to do with composition, which is the only thing those
tests ask about.

The last test strips them and keeps `GenericWebHostService`, which is what builds the pipeline and
therefore the route table the test came for. The gate still runs, because it is the host's and not
a hosted service.
