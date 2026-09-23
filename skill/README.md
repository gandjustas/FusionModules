# The FusionModules skill

Instructions for an AI agent migrating a .NET solution to a modular monolith, or building one
from scratch.

## One copy, three shapes

This directory is a Claude Code plugin, and the skill inside it is authored in place:

| | |
|---|---|
| [`skills/fusion-modules/`](skills/fusion-modules) | The skill, and the only authored copy. Also an [Agent Skills](https://agentskills.io) directory as it stands, which is what every other agent that supports skills loads. |
| [`commands/`](commands), [`.claude-plugin/plugin.json`](.claude-plugin/plugin.json) | The rest of the plugin: five slash commands and the manifest. Listed by [`/.claude-plugin/marketplace.json`](../.claude-plugin/marketplace.json), which makes the repository its own marketplace. |
| [`dist/SKILL.md`](dist/SKILL.md) | One portable file, references inlined and cross-links rewritten as anchors. For anything that takes a single markdown instruction file. Generated. |
| [`dist/fusion-modules-rules.md`](dist/fusion-modules-rules.md) | The routing layer alone, ~70 lines. For a Cursor rule, `AGENTS.md` or a `copilot-instructions.md`, where everything is always in context and length is the whole cost. Generated. |

The two in `dist/` are rebuilt from the skill and must not be edited:

```bash
dotnet run --project ../tools/skillgen
```

CI regenerates them and fails if they changed, so they cannot drift. The plugin used to be
generated too, as a copy of the skill that git ignored — which is why installing it from GitHub
delivered five commands and no skill. Making the plugin's directory the source removed both the
copy and the generator step it needed.

The manifest has no `version`, and CI fails if it grows one. A marketplace install is then
versioned by commit, so every push to the default branch reaches it; with a version in the file,
nothing would until somebody bumped it. The release zip is the one place with a tag behind it, and
`release.yml` stamps the tag into the copy it zips.

## Using it

### Claude Code

```
/plugin marketplace add gandjustas/FusionModules
/plugin install fusion-modules@fusion-modules
```

The skill loads on its own when the description matches. The commands are
`/fusion-modules:assess`, `/fusion-modules:migrate`, `/fusion-modules:verify`,
`/fusion-modules:new-module` and `/fusion-modules:explain`.

To offer it to everyone who opens a particular solution, put this in that repository's
`.claude/settings.json`. Claude Code then asks each person to install it when they trust the folder:

```json
{
  "extraKnownMarketplaces": {
    "fusion-modules": { "source": { "source": "github", "repo": "gandjustas/FusionModules" } }
  },
  "enabledPlugins": { "fusion-modules@fusion-modules": true }
}
```

Without a marketplace: `claude --plugin-dir skill` from a clone loads it for one session, and so
does `claude --plugin-dir fusion-modules` from the unpacked `fusion-modules-plugin.zip` attached to
each release. Copying `skills/fusion-modules/` into `.claude/skills/` or `~/.claude/skills/` keeps
the skill and drops the commands.

### Other agents

Copy [`skills/fusion-modules/`](skills/fusion-modules) whole, keeping the directory name — the
standard requires it to match the skill's `name`:

| | |
|---|---|
| Any agent that follows the standard | `.agents/skills/fusion-modules/` in the repository, or `~/.agents/skills/fusion-modules/` |
| GitHub Copilot, VS Code | `.github/skills/` also works |
| Cursor | `.cursor/skills/` also works |
| Codex | `.codex/skills/` also works. `~/.codex/skills/` is still read, but deprecated in favour of `~/.agents/skills/`. Invoked as `$fusion-modules`; restart after copying |
| OpenCode | `.opencode/skills/` and `~/.config/opencode/skills/`; also reads `.claude/skills/`, so a skill copied there for Claude Code is already found |
| Gemini CLI | `.gemini/skills/` also works |
| ZCode | `~/.zcode/skills/` only; into a project through **Settings → Skills → Import**. Invoked as `$fusion-modules` |

```bash
git clone --depth 1 https://github.com/gandjustas/FusionModules
mkdir -p .agents/skills
cp -r FusionModules/skill/skills/fusion-modules .agents/skills/
```

Codex also reads a marketplace from `.claude-plugin/marketplace.json` and a plugin from
`.claude-plugin/plugin.json`, so the repository installs there as a plugin too. Only the skill
arrives: a Codex plugin carries skills, hooks, MCP servers and apps, and not slash commands.

```bash
codex plugin marketplace add gandjustas/FusionModules
codex plugin add fusion-modules@fusion-modules
```

The directory beats the flattened file wherever skills are supported: the agent reads the
description up front and a reference when a phase needs it, instead of carrying 70 KB into every
turn.

A tool without skills takes [`dist/SKILL.md`](dist/SKILL.md) as an instruction file, or
[`dist/fusion-modules-rules.md`](dist/fusion-modules-rules.md) in its always-on rules. The rules
file names the phases and nothing more, so it is worth pairing with a link to the full skill.

## What is in it

[`SKILL.md`](skills/fusion-modules/SKILL.md) routes; the references carry the detail.

| | |
|---|---|
| [concepts](skills/fusion-modules/references/concepts.md) | The mechanism, and the three consequences people get wrong |
| [assess](skills/fusion-modules/references/assess.md) | The inventory, the blockers to escalate, the questions only a human can answer |
| [host](skills/fusion-modules/references/host.md) | Two directions; what belongs in the host and what does not |
| [modules](skills/fusion-modules/references/modules.md) | Converting a service; five silent collisions; visibility |
| [data](skills/fusion-modules/references/data.md) | The EF Core recipe, composing modules, three migration playbooks |
| [transport](skills/fusion-modules/references/transport.md) | Contracts, in-process versus remote, and why messaging is not a method call |
| [deployment](skills/fusion-modules/references/deployment.md) | One image, topologies as environment variables |
| [verify](skills/fusion-modules/references/verify.md) | The loop, run after every phase |
| [troubleshooting](skills/fusion-modules/references/troubleshooting.md) | By symptom, because the symptoms are all silent |

[`assets/`](skills/fusion-modules/assets) holds the templates the skill writes into a project,
and the inventory script it runs in Phase 0.

## What it will not do

- Run `dotnet ef database update`, `psql`, or anything else against a database that is not a
  disposable local container. It writes the SQL and the plan; a human runs them.
- Collapse a message queue into a method call without an explicit decision from the user, with
  the change in delivery semantics written down.
- Decide module boundaries, module names, which transports to keep, or how to consolidate
  databases. Those are deployment contracts and they are the user's to make.
