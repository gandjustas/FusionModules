---
name: modulith
description: >-
  Build or migrate to a modular monolith on ASP.NET Core HostingStartup modules using the Modulith
  package. Use when the user wants to merge .NET microservices into one process or one image, cut
  network hops, latency or infrastructure between services they own, split a monolith into
  independently deployable modules, choose deployment topology with an environment variable
  instead of a rebuild, replace HttpClient/gRPC/RabbitMQ calls between their own services with
  in-process calls, consolidate per-service DbContexts or EF Core migrations, or when they mention
  Modulith, ModuleBase, IHostingStartup, HOSTINGSTARTUPASSEMBLIES, IStartupFilter modules, a
  modular monolith, or "modularity without microservices".
---

# Modular monoliths on ASP.NET Core

A module is an ordinary class library carrying `[assembly: HostingStartup(typeof(Module))]`. It is
activated by naming its assembly in `HOSTINGSTARTUPASSEMBLIES`. One image serves every deployment
topology, and which modules are live is a deployment decision rather than a build decision.

The host has no compile-time knowledge of any module. That is the property everything else rests
on, and the thing to protect when in doubt.

**Read [How it works](references/concepts.md) before changing any code.** The mechanism is small
but not obvious, and most mistakes here fail silently rather than loudly.

## Which direction

**Monolith → modules.** The existing `Program.cs` is not touched. Add the package, make sure
`UseRouting()` is there, then move one `app.MapX` block and its registrations into a module at a
time. The application works after every step. This is the low-risk direction and the one to
prefer when both are on the table.

**Microservices → modules.** Start a new empty host rather than promoting one of the services:
it inherits no references, so MOD0003 is green from the first build. Then convert services
lowest-fan-in first.

**New system.** Start from [samples/01-minimal-api](https://github.com/gandjustas/modulith/tree/main/samples/01-minimal-api)
and skip the assessment.

## Phases

Work through these in order. Each has a gate that must pass before the next begins.

| | | |
|---|---|---|
| 0 | [Assess](references/assess.md) | Read-only. Produces `modulith-assessment.md` and a list of decisions only a human can make. |
| 1 | [Host](references/host.md) | A host that serves nothing. Gate: it runs and 404s. |
| 2 | [Modules](references/modules.md) | One service or feature at a time. Gate: route inventory unchanged, per module. |
| 3 | [Data](references/data.md) | Entities into the modules that own them; one model composed at startup. **Never runs destructive database commands.** |
| 4 | [Transports](references/transport.md) | Contracts out, in-process implementations in, remote ones kept only where they earn it. |
| 5 | [Deployment](references/deployment.md) | One image, topologies as environment variables. |
| 6 | [Verify](references/verify.md) | Run after *every* phase, not at the end. |

State lives in `modulith-migration.md` at the repository root: the phase, the decisions taken and
their reasons, and the status of each service. Write to it as you go, so a resumed session does
not re-ask questions the user has already answered.

## Rules of engagement

**Ask before deciding what only the user can decide.** Module boundaries and module names become
`HOSTINGSTARTUPASSEMBLIES` values, which are a deployment contract and effectively permanent. So
are the answers about which transports to keep and how to consolidate databases. Put the
questions in one numbered batch at the end of Phase 0, record the answers, and do not re-derive
them later.

**Escalate blockers, do not work around them.** See [Assess](references/assess.md) for the list.
A service that exists *because* it is isolated — for compliance, for a different scaling profile,
for a different release cadence — does not become a module because merging is technically
possible.

**Never collapse asynchronous messaging silently.** Turning a durable publish into a method call
changes at-least-once into at-most-once and removes retry, dead-lettering, backpressure and
failure isolation. Every messaging edge gets an explicit decision from the user with the
semantics delta written down. See [Transports](references/transport.md).

**Never run a destructive database command.** Write the SQL and the plan; the user runs them.
`dotnet ef database update`, `psql`, and anything touching a database that is not a disposable
local container are out of scope for this skill.

**Preserve the route inventory — and do not stop at it.** It is the strongest available signal
that a mechanical migration is correct, and it is blind to everything resolved by reflection,
negotiated on the wire, or built after the container is. Capture it before touching anything, and
treat booting every topology with one real request per module as its peer rather than its
follow-up. See [Verification](references/verify.md).

## When something fails quietly

Nearly every failure mode of this approach is silent: the application starts, the probe passes,
and an endpoint is simply missing. See [Troubleshooting](references/troubleshooting.md), which
lists them by symptom along with what each MOD diagnostic means.
