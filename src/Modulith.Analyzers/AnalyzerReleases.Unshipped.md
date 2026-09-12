; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MOD0001 | Modulith.Design | Error | A module must not expose public types
MOD0002 | Modulith.Design | Error | The type named by HostingStartup must be a module
MOD0003 | Modulith.Design | Error | The host must not use types from a module
MOD0004 | Modulith.Design | Error | The host must not declare an ApplicationPart for a module
MOD0005 | Modulith.Design | Error | A module must be named by an assembly-level HostingStartup attribute
MOD0020 | Modulith.Design | Warning | The Modulith package is not referenced
