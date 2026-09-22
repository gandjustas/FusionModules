# Phase 0 — Assess

Read-only. Change nothing. The output is `fusion-modules-assessment.md` and one batch of questions.

The risk in this phase is not being wrong, it is being inconsistent — looking at different things
in different repositories and reaching confident conclusions from an incomplete picture. The
checklist below is what to look at, every time.

Do that inventory yourself: grep reads a solution better than a script does, because it sees the
context around each hit. Run the script only for the two things reading cannot find:

```bash
pwsh assets/scripts/assess.ps1 -Path <solution-root> -Output fusion-modules-collisions.json
```

- the same configuration key holding **different values** in different files
- the same route template declared in more than one place

Both are silent at runtime, and which one wins is decided by load order — which is decided by
`HOSTINGSTARTUPASSEMBLIES`, and can therefore differ between topologies.

Two caveats. It composes no routes: `MapGroup("/billing")` followed by `MapGet("/overdue")` is
reported as two templates, not one. And its collisions are candidates — two modules declaring the
same template only matters if some topology loads both. Confirm before raising. The authoritative
route inventory comes from `EndpointDataSource` at runtime; see [verify.md](verify.md).

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

**What crosses an assembly boundary by reflection.** One list, and the cheapest question in this
phase. SignalR hubs *and the client interface of every `Hub<TClient>`*, `JsonSerializerContext`
types and what they bind, gRPC service bases, `ActivatorUtilities` over a type named in
configuration, anything an assembly scan picks up, anything resolved by a string from a settings
file. Each one is a type whose visibility stops being a free choice when its service becomes a
module, and none of them fail at build time — MOD0001 will tell you to make them internal and the
compiler will agree. Phase 2 spends this list; collect it while you are already reading the code.

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
and record the answers in `fusion-modules-migration.md` with their reasons.

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
