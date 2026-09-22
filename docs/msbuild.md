# MSBuild properties

The `FusionModules` package ships a `.targets` file that is imported at the bottom of every project
that references it. It sets four things, each of them a fix for something that otherwise fails
quietly. All four can be overridden, and each override is listed here with the reason you would
reach for it.

## `FusionModulesProjectKind`

`Test`, `Host` or `Module`. Everything else on this page keys off it, and the analyzers read it
through `CompilerVisibleProperty` — MOD0003 holds a host to a rule a test project is exempt from.

Computed when unset, in this order:

| Condition | Kind |
|---|---|
| `IsTestProject` is `true` | `Test` |
| `OutputType` is `Exe` or `WinExe` | `Host` |
| otherwise | `Module` |

Test projects are checked first because they have an entry point too and would otherwise be held to
the host's rules. A test legitimately reaches for a module's types: arranging data through an entity
is the normal way to write an integration test.

Set it explicitly when the inference is wrong — a test project whose SDK does not set
`IsTestProject`, or a console tool that is not a host:

```xml
<PropertyGroup>
  <FusionModulesProjectKind>Test</FusionModulesProjectKind>
</PropertyGroup>
```

## `FusionModulesConfigureMvcApplicationParts`

Default on. For a `Host`, sets `GenerateMvcApplicationPartsAssemblyAttributes=false`.

Application Part Discovery does not know about `HostingStartup`: left alone, the SDK emits an
`[ApplicationPart]` for every referenced MVC-flavoured project, so the host picks up a module's
controllers and pages without running the module's code and regardless of
`HOSTINGSTARTUPASSEMBLIES`. That is [MOD0004](rules/MOD0004.md), and each module calls
`AddApplicationPart` for itself instead.

Set it to `false` only if you are managing `GenerateMvcApplicationPartsAssemblyAttributes` yourself.
Turning it off and leaving that property alone puts every module's controllers into every topology,
and nothing will say so.

## `AddRazorSupportForMvc`

Set to `true` for a `Module` on `Microsoft.NET.Sdk.Razor` when the project has not set it.

A module that ships `.cshtml` is always a library compiled for an MVC host, so it always needs
Razor's MVC support. Left unset, the Razor SDK emits RAZORSDK1004 and the views do not work. Razor
Pages modules hit this as readily as MVC ones, despite the property's name.

This one is set only when you have not, so there is no separate opt-out.

## `FusionModulesImplicitUsings`

Default on. For a `Module` on a non-Web SDK with `ImplicitUsings` enabled, contributes the implicit
usings the Web SDK would have:

`System.Net.Http.Json`, `Microsoft.AspNetCore.Builder`, `Microsoft.AspNetCore.Hosting`,
`Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, `Microsoft.Extensions.Configuration`,
`Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`,
`Microsoft.Extensions.Logging`.

Converting a service from `Microsoft.NET.Sdk.Web` to `Microsoft.NET.Sdk` silently drops those, so
the first build of the converted project is a wall of CS0246 on types that are sitting right there
in the framework reference. It reads like a missing package reference and it is not one.

Set it to `false` if you would rather write the `using` directives out, or if a name collides with
one of your own:

```xml
<PropertyGroup>
  <FusionModulesImplicitUsings>false</FusionModulesImplicitUsings>
</PropertyGroup>
```

A module still on `Microsoft.NET.Sdk.Web` is skipped automatically — it already has these, and a
duplicate global using is CS0105.

## Properties the samples use

Not part of the package. They belong to this repository's own build and are documented here because
the sample projects reference them:

| Property | Meaning |
|---|---|
| `FusionModulesUseProjectReferences` | `true` (default) consumes FusionModules by project reference; `false` by package reference from `local-feed`. CI runs both. |
| `FusionModulesVersion` | The package version to use when the above is `false`. |
