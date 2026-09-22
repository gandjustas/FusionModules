# The FusionModules skill

Instructions for an AI agent migrating a .NET solution to a modular monolith, or building one
from scratch.

## Three shapes, one source

Everything is authored in [`src/`](src). The other two are generated and must not be edited:

```bash
dotnet run --project ../tools/skillgen
```

| | |
|---|---|
| [`src/`](src) | The authored form, and the only committed copy. |
| `plugin/` | A Claude Code plugin: the skill plus five slash commands. Generated — `plugin/skills/` is not committed, and the release attaches it as a zip. |
| [`dist/SKILL.md`](dist/SKILL.md) | One portable file, references inlined and cross-links rewritten as anchors. For anything that takes a single markdown instruction file. |
| [`dist/fusion-modules-rules.md`](dist/fusion-modules-rules.md) | The routing layer alone, ~70 lines. For a Cursor rule or a `copilot-instructions.md`, where everything is always in context and length is the whole cost. |

CI regenerates and fails if `dist/` changed, so the portable copies cannot drift. `plugin/skills/`
is generated but not committed: it was a byte-for-byte copy of `src/`, and the only thing keeping
two identical trees in git bought was a way to edit the wrong one.

`plugin/.claude-plugin/plugin.json` and `plugin/commands/` are hand-written and survive
regeneration. CI checks the manifest parses and has its required fields, because nothing else
would notice it going malformed until somebody installed the released zip.

The `version` in it is a placeholder: a working tree does not know what the next tag will be, so
`release.yml` stamps the tag into the copy it zips and MinVer stays the only source of the number.
A zip built from a `workflow_dispatch` run has no tag behind it and keeps the placeholder, which is
the honest answer for a build that is not a release.

## Using it

**Claude Code** — run the generator, then add `plugin/` as a marketplace or copy
`plugin/skills/fusion-modules/` into `.claude/skills/`. Releases attach it as a zip. Commands:
`/fusion-modules:assess`, `/fusion-modules:migrate`, `/fusion-modules:verify`, `/fusion-modules:new-module`,
`/fusion-modules:explain`.

**Anything else** — hand it `dist/SKILL.md`, or drop `dist/fusion-modules-rules.md` into the tool's
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
