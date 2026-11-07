; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 0.2.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MOD0001 | FusionModules.Design | Error | A module must not expose public types
MOD0002 | FusionModules.Design | Error | The type named by HostingStartup must be a module
MOD0003 | FusionModules.Design | Error | A module must be named by an assembly-level HostingStartup attribute
MOD0004 | FusionModules.Design | Error | The host must not use types from a module
MOD0005 | FusionModules.Design | Error | A library that is not a module must not use types from a module
MOD0006 | FusionModules.Design | Error | The host's builder does not load modules
MOD0007 | FusionModules.Design | Error | The host must not declare an ApplicationPart for a module
MOD0008 | FusionModules.Design | Error | A module must not replace the application pipeline
MOD0009 | FusionModules.Design | Error | A module must not register itself as a startup filter
MOD0010 | FusionModules.Design | Error | A hub's client interface must be reachable from SignalR's generated proxy
MOD0011 | FusionModules.Design | Error | MVC never discovers the module's controllers or pages
MOD0012 | FusionModules.Usage | Warning | The host does not validate its modules
MOD0013 | FusionModules.Design | Warning | The FusionModules package is not referenced
