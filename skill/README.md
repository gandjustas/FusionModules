# The Modulith skill

Instructions for an AI agent migrating a .NET solution to a modular monolith, or building one
from scratch.

## Three shapes, one source

Everything is authored in [`src/`](src). The other two are generated and must not be edited:

```bash
dotnet run --project ../tools/skillgen
```

| | |
|---|---|
| [`src/`](src) | The authored form. Also the plugin's shape, so the plugin build is a copy. |
| [`plugin/`](plugin) | A Claude Code plugin: the skill plus five slash commands. |
| [`dist/SKILL.md`](dist/SKILL.md) | One portable file, references inlined and cross-links rewritten as anchors. For anything that takes a single markdown instruction file. |
| [`dist/modulith-rules.md`](dist/modulith-rules.md) | The routing layer alone, ~70 lines. For a Cursor rule or a `copilot-instructions.md`, where everything is always in context and length is the whole cost. |

CI runs the generator and fails if anything changed, so the outputs cannot drift.

`plugin/.claude-plugin/plugin.json` and `plugin/commands/` are hand-written and survive
regeneration; only `plugin/skills/modulith/` is rebuilt.

## Using it

**Claude Code** — add the plugin directory as a marketplace or copy `plugin/skills/modulith/`
into `.claude/skills/`. Commands: `/modulith:assess`, `/modulith:migrate`, `/modulith:verify`,
`/modulith:new-module`, `/modulith:explain`.

**Anything else** — hand it `dist/SKILL.md`, or drop `dist/modulith-rules.md` into the tool's
always-on rules file.

## What is in it

[`src/SKILL.md`](src/SKILL.md) routes; the references carry the detail.

| | |
|---|---|
| [concepts](src/references/concepts.md) | The mechanism, and the three consequences people get wrong |
| [assess](src/references/assess.md) | The inventory, the blockers to escalate, the questions only a human can answer |
| [host](src/references/host.md) | Two directions; what belongs in the host and what does not |
| [modules](src/references/modules.md) | Converting a service; five silent collisions; visibility |
| [data](src/references/data.md) | The EF Core recipe, composing modules, three migration playbooks |
| [transport](src/references/transport.md) | Contracts, in-process versus remote, and why messaging is not a method call |
| [deployment](src/references/deployment.md) | One image, topologies as environment variables |
| [verify](src/references/verify.md) | The loop, run after every phase |
| [troubleshooting](src/references/troubleshooting.md) | By symptom, because the symptoms are all silent |

[`src/assets/`](src/assets) holds the templates the skill writes into a project, and the
inventory script it runs in Phase 0.

## What it will not do

- Run `dotnet ef database update`, `psql`, or anything else against a database that is not a
  disposable local container. It writes the SQL and the plan; a human runs them.
- Collapse a message queue into a method call without an explicit decision from the user, with
  the change in delivery semantics written down.
- Decide module boundaries, module names, which transports to keep, or how to consolidate
  databases. Those are deployment contracts and they are the user's to make.
