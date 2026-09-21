---
description: Scaffold a new module and wire it into the host and tests
---

Create a new module named $ARGUMENTS, using the `modulith` skill's `Module.cs` template.

- a project on `Microsoft.NET.Sdk` (or `Microsoft.NET.Sdk.Razor` if it will ship `.cshtml`), with
  an explicit `<AssemblyName>`, referencing the `Modulith` package
- `[assembly: HostingStartup(typeof(Module))]` and a `Module : ModuleBase`
- a plain project reference from the host **and** from the test project — the host never uses the
  module's types, the reference is only what orders the build and copies the assembly next to the
  executable so it can be loaded by name
- a launch profile that loads it, and a smoke test that boots it alone

Ask where it should live and what it owns before creating anything, unless the answer is obvious
from the existing layout.
