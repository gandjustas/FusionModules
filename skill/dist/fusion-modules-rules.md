<!-- Generated from skill/src/SKILL.md. The routing layer only; the phases it names are detailed in the full skill at https://github.com/gandjustas/FusionModules/blob/HEAD/skill/dist/SKILL.md -->

# Modular monoliths on ASP.NET Core

A module is an ordinary class library carrying `[assembly: HostingStartup(typeof(Module))]`. It is
activated by naming its assembly in `HOSTINGSTARTUPASSEMBLIES`. One image serves every deployment
topology, and which modules are live is a deployment decision rather than a build decision.

The host has no compile-time knowledge of any module. That is the property everything else rests
on, and the thing to protect when in doubt.

**Read How it works before changing any code.** The mechanism is small
but not obvious, and most mistakes here fail silently rather than loudly.

## Which direction

**Monolith → modules.** The existing `Program.cs` is not touched. Add the package, make sure
`UseRouting()` is there, then move one `app.MapX` block and its registrations into a module at a
time. The application works after every step. This is the low-risk direction and the one to
prefer when both are on the table.

**Microservices → modules.** Start a new empty host rather than promoting one of the services:
it inherits no references, so MOD0003 is green from the first build. Then convert services
lowest-fan-in first.

**New system.** Start from [samples/01-minimal-api](https://github.com/gandjustas/FusionModules/tree/HEAD/samples/01-minimal-api)
and skip the assessment.

## Phases

Work through these in order. Each has a gate that must pass before the next begins.

| | | |
|---|---|---|
| 0 | Assess | Read-only. Produces `fusion-modules-assessment.md` and a list of decisions only a human can make. |
| 1 | Host | A host that serves nothing. Gate: it runs and 404s. |
| 2 | Modules | One service or feature at a time. Gate: route inventory unchanged, per module. |
| 3 | Data | Entities into the modules that own them; one model composed at startup. **Never runs destructive database commands.** |
| 4 | Transports | Contracts out, in-process implementations in, remote ones kept only where they earn it. |
| 5 | Deployment | One image, topologies as environment variables. |
| 6 | Verify | Run after *every* phase, not at the end. |

State lives in `fusion-modules-migration.md` at the repository root: the phase, the decisions taken and
their reasons, and the status of each service. Write to it as you go, so a resumed session does
not re-ask questions the user has already answered.

## Rules of engagement

**Ask before deciding what only the user can decide.** Module boundaries and module names become
`HOSTINGSTARTUPASSEMBLIES` values, which are a deployment contract and effectively permanent. So
are the answers about which transports to keep and how to consolidate databases. Put the
questions in one numbered batch at the end of Phase 0, record the answers, and do not re-derive
them later.

**Escalate blockers, do not work around them.** See Assess for the list.
A service that exists *because* it is isolated — for compliance, for a different scaling profile,
for a different release cadence — does not become a module because merging is technically
possible.

**Never collapse asynchronous messaging silently.** Turning a durable publish into a method call
changes at-least-once into at-most-once and removes retry, dead-lettering, backpressure and
failure isolation. Every messaging edge gets an explicit decision from the user with the
semantics delta written down. See Transports.

**Never run a destructive database command.** Write the SQL and the plan; the user runs them.
`dotnet ef database update`, `psql`, and anything touching a database that is not a disposable
local container are out of scope for this skill.

**Preserve the route inventory — and do not stop at it.** It is the strongest available signal
that a mechanical migration is correct, and it is blind to everything resolved by reflection,
negotiated on the wire, or built after the container is. Capture it before touching anything, and
treat booting every topology with one real request per module as its peer rather than its
follow-up. See Verification.

## When something fails quietly

Nearly every failure mode of this approach is silent: the application starts, the probe passes,
and an endpoint is simply missing. See Troubleshooting, which
lists them by symptom along with what each MOD diagnostic means.
