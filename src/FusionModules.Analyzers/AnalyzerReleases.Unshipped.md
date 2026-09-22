; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MOD0001 | FusionModules.Design | Error | A module must not expose public types
MOD0002 | FusionModules.Design | Error | The type named by HostingStartup must be a module
MOD0003 | FusionModules.Design | Error | The host must not use types from a module
MOD0004 | FusionModules.Design | Error | The host must not declare an ApplicationPart for a module
MOD0005 | FusionModules.Design | Error | A module must be named by an assembly-level HostingStartup attribute
MOD0006 | FusionModules.Design | Error | A module must not replace the application pipeline
MOD0007 | FusionModules.Usage | Warning | Redundant IStartupFilter registration
MOD0008 | FusionModules.Usage | Warning | A hosted service in a module runs in every replica that loads it
MOD0009 | FusionModules.Design | Error | A hub's client interface must be reachable from SignalR's generated proxy
MOD0020 | FusionModules.Design | Warning | The FusionModules package is not referenced
