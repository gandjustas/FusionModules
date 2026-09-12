---
description: Convert one service or feature into a module
---

Continue the `modulith` migration for $ARGUMENTS.

Read `modulith-migration.md` first. If it does not exist, the assessment has not been done — run
`/modulith:assess` instead and stop.

Convert exactly one service or feature, following the skill's Modules reference: project shape,
`Program.cs` into `Module.cs`, the five silent collisions, visibility. Then run the per-module
gate — build with `-warnaserror`, compare the route inventory, add the smoke tests — and record
what was done in `modulith-migration.md`.

One at a time. Do not start a second while the first is unfinished.
