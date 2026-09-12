# Phase 0 — Assess

Read-only. Change nothing. The output is `modulith-assessment.md` and one batch of questions.

The risk in this phase is not being wrong, it is being inconsistent — looking at different things
in different repositories and reaching confident conclusions from an incomplete picture. Run the
inventory script first and reason over its output rather than grepping ad hoc.

```bash
pwsh assets/scripts/assess.ps1 -Path <solution-root> -Output modulith-assessment.json
```

Needs `pwsh`, which is cross-platform. Without it, gather the same things by hand — the list
below is the checklist either way.

Three things it does not do, so do not take its silence as an answer:

- **It composes no routes.** `MapGroup("/billing")` followed by `MapGet("/overdue")` is reported
  as two templates, not one. The reliable route inventory comes from `EndpointDataSource` at
  runtime — see [verify.md](verify.md). This list is for spotting collisions early, not for the
  baseline.
- **Its collisions are candidates.** Two modules mapping the same template only matters if a
  topology loads both. Check before raising it.
- **Properties inherited from `Directory.Build.props` are reported separately**, not resolved.
  Resolving them properly means an MSBuild evaluation per project, which turns seconds into
  minutes. So a project whose `targetFramework` is empty probably inherits it.

## What the assessment must contain

**Services.** Project, SDK, target framework, entry point, assembly name, what it serves.

**The call graph between them.** One row per edge: caller, callee, transport (HTTP / gRPC /
queue / database), the contract type, and the call site as `file:line`. This is the input to
Phase 4 and the thing most likely to be incomplete — check service discovery configuration and
compose files as well as code, because an edge can exist entirely in configuration.

**Data.** Per service: `DbContext` types, provider, connection string, migrations assembly and
history table, and whether any two services share a database. Two services on one database is a
different migration than two services on two databases.

**Deployment.** Images, Dockerfiles, compose/Kubernetes/Aspire definitions, CI jobs per service,
replica counts and any autoscaling rules. Replica counts matter: they are the evidence for
whether a service genuinely has its own scaling profile.

**Cross-cutting, as a diff table.** Authentication schemes, authorization policies, CORS, rate
limiting, OpenTelemetry, health checks, problem details, localization — and the middleware order
each service uses. Divergent middleware order is the single largest source of behaviour change
after a merge, and it is invisible in a per-service reading. Lay them side by side.

**Collisions.** Three kinds, all silent:

- *Routes.* Enumerate every template across every service and list the duplicates. Never merge
  two services with a known route collision; resolve it with `MapGroup` or an area first.
- *Configuration keys.* The same key with different values in two services means one of them
  changes behaviour after the merge, and nothing will say so.
- *DI registrations.* A non-`TryAdd` registration of the same service type in two modules means
  the winner is decided by `HOSTINGSTARTUPASSEMBLIES` order. List every interface registered more
  than once.

**Proposed module boundaries and topologies**, with the reasoning.

**Risks**, including the ones below.

## Blockers — escalate, do not work around

Stop and raise these with the user. Some are fatal to the whole idea; all of them change the
plan.

| | |
|---|---|
| Mixed target frameworks | One process, one runtime. Align first or exclude the service. |
| A non-.NET service on the boundary | It stays remote. The question is only which edges change. |
| Owned by another team | Their release cadence becomes yours. That is an organisational decision. |
| Divergent authentication on colliding routes | Merging changes who can reach what. Needs explicit design. |
| Process-wide state or configuration | `ServicePointManager`, thread culture, `AppContext` switches, static caches, process-wide logging configuration. Two services with different settings cannot both be right in one process. |
| Different database engines | Separate contexts and separate migration histories, whatever else happens. |
| Sagas and compensating transactions | The transaction boundaries change. Do not touch these without designing the new boundaries. |
| Different scaling profiles | One CPU-bound service among IO-bound ones is a reason to keep it separate — or to give it a topology of its own. |
| Isolation as the reason it exists | Compliance, blast radius, tenancy. Technically mergeable is not the same as should be merged. |
| Different release cadence or SLA | Merging couples them. Say so before anything else. |
| Background services | A hosted service now runs in every replica of every topology that loads its module. See below. |

### Background services deserve their own paragraph

A worker that ran once — one replica of one service — now runs wherever its module is loaded. If
two replicas of a merged topology both load it, it runs twice. This changes behaviour silently
and in production. The options are leader election, a dedicated worker topology that is the only
one loading that module, or leaving the service alone. Ask.

## Human gate

End Phase 0 with one numbered batch of questions. Do not start Phase 1 before they are answered,
and record the answers in `modulith-migration.md` with their reasons.

1. **Module boundaries and names.** The names become `HOSTINGSTARTUPASSEMBLIES` values — a
   deployment contract, and effectively permanent. Propose a set; ask for confirmation.
2. **Target topologies.** Which combinations of modules are to be deployable? There is usually
   an all-in one plus the shapes that exist today.
3. **Transports to keep.** Per edge in the call graph. Default to removing edges whose only
   callers are inside this solution, keeping everything else.
4. **Database consolidation.** One database or several; if several, which contexts go where.
   See [data.md](data.md) for the three migration playbooks.
5. **Contract ownership.** For each type that crosses a module boundary: which contracts library
   owns it, and who may change it.

Ask anything else the assessment turned up that has more than one defensible answer. It is
cheaper to ask now than to unpick a decision three phases later.
