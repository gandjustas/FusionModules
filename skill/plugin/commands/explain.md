---
description: Explain a MOD diagnostic and how to fix it in this codebase
---

Explain diagnostic $ARGUMENTS from the `modulith` skill's Troubleshooting reference: what it
means, why the rule exists, and what the failure looks like if it is suppressed rather than
fixed.

Then find the actual occurrences in this repository and propose the fix for each — which is often
different per site. MOD0001 in particular has several correct answers: make it internal, move it
to a contracts library, or allow it in `.editorconfig` with a recorded reason.
