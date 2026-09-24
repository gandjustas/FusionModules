# MSBuild properties

The `FusionModules` package ships a `.targets` file that is imported at the bottom of every project
that references it. It sets five things, each of them a fix for something that otherwise fails
quietly. All five can be overridden, and each override is listed here with the reason you would
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

## `FusionModulesAspNetCoreAnalyzers`

Default on. For a `Module` on a non-Web SDK, adds the two analyzer assemblies the Web SDK would
have added: `Microsoft.AspNetCore.Analyzers.dll` and `Microsoft.AspNetCore.Mvc.Analyzers.dll`.

Between them they carry five rules: the Startup analyzer (ASP0000, ASP0001) and MVC1000 through
MVC1006 — `IHtmlHelper.Partial`, attributes that do nothing on a page model, a parameter name that
shadows a bound property, a tag helper in a code block. An MVC or Razor Pages module is exactly the
project that still needs them.

Everything in `Microsoft.AspNetCore.App.Ref` arrives with the framework reference instead, on any
SDK, so the whole ASP0xxx family about minimal APIs and routing keeps working without this. Measured
on .NET 10: a module keeps sixteen analyzers and generators either way and loses only these two
assemblies.

`Microsoft.AspNetCore.Mvc.Api.Analyzers` is not included. The Web SDK adds it only under
`IncludeOpenAPIAnalyzers`, and .NET 10 deprecates both (ASPDEPR007).

Set it to `false` to keep a module on the rules the framework reference brings and nothing more:

```xml
<PropertyGroup>
  <FusionModulesAspNetCoreAnalyzers>false</FusionModulesAspNetCoreAnalyzers>
</PropertyGroup>
```

The SDK's own `DisableImplicitAspNetCoreAnalyzers` turns this off too, so a project that already
sets it keeps meaning what it meant.

## `FusionModulesWebSdkAnalyzerPath`

The folder the two assemblies above are read from. Computed as
`$(NetCoreRoot)sdk/$(NETCoreSdkVersion)/Sdks/Microsoft.NET.Sdk.Web/analyzers/cs/`, and each file is
referenced only if it is there.

Not `$(MSBuildSDKsPath)`, which is the obvious spelling and the wrong one: under Visual Studio's
MSBuild that resolves to the IDE's own `Sdks` folder, which holds two SDKs and neither is the Web
SDK, so the analyzers would go missing in the IDE and nowhere else. Set this property if your layout
puts them somewhere else:

```xml
<PropertyGroup>
  <FusionModulesWebSdkAnalyzerPath>/opt/sdk-analyzers/</FusionModulesWebSdkAnalyzerPath>
</PropertyGroup>
```

Trailing separator included — the file name is appended to it directly.

## `FusionModulesKnownModules`

Default on. For a `Host` or a `Test` project, generates `FusionModules.KnownModules`: every module
the project references, as a constant.

```csharp
internal static class KnownModules
{
    public const string All = "Customers.Entities;CustomersModule;Orders.Entities;BillingModule";

    public static string[] Names => new string[] { "Customers.Entities", /* … */ };
}
```

The case it exists for is `IDesignTimeDbContextFactory`. `dotnet ef` builds the host, so
HostingStartup runs and the model follows `HOSTINGSTARTUPASSEMBLIES` as it stood in the shell that
ran the command — empty when unset, one topology's tables when set, and no error either way. A
factory therefore names the modules itself, and a hand-written list is a copy of the project file
that nothing keeps honest: add a module, forget the array, and the next migration is missing its
tables without a word.

Dependencies come first, ties broken by name. That is what puts a composing module after the
modules it joins, which is the order EF Core applies entity configurations in — and it is derived
from the reference graph, so it follows the code rather than a convention about naming.

A module never gets it: a module has no business knowing the topology it will be deployed in.
Without [`FusionModulesProjectKind`](#fusionmodulesprojectkind) — that is, without this package's
targets — a project that produces a program gets it and a library does not.

Set it to `false` to turn the generator off:

```xml
<PropertyGroup>
  <FusionModulesKnownModules>false</FusionModulesKnownModules>
</PropertyGroup>
```

## Properties the samples use

Not part of the package. They belong to this repository's own build and are documented here because
the sample projects reference them:

| Property | Meaning |
|---|---|
| `FusionModulesUseProjectReferences` | `true` (default) consumes FusionModules by project reference; `false` by package reference from `local-feed`. CI runs both. |
| `FusionModulesVersion` | The package version to use when the above is `false`. |
