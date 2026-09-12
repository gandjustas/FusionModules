---
description: Run the Modulith verification loop
---

Run the verification loop from the `modulith` skill against the current repository.

Build with `-warnaserror` and report any MOD diagnostics. Boot every topology — no modules, each
module alone, the full set — and diff each one's route inventory against the stored baseline.
Check the model snapshot per topology and that no migration is pending. Check the three collision
classes: duplicate configuration keys with different values, non-`TryAdd` registrations of the
same service type in two modules, and duplicate route templates.

Report what passed and what did not. Fix nothing without being asked.
