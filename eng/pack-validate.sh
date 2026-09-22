#!/usr/bin/env bash
#
# Builds the package and consumes it the way a stranger would.
#
# In-repo builds prove nothing about packaging: analyzers and MSBuild assets are wired up by
# ProjectReference there, and the layout under analyzers/dotnet/cs and buildTransitive is only
# exercised once the package is restored. That layout breaks silently — the build stays green and
# the rules simply never run — so this is the job that catches it.
#
# Usage: eng/pack-validate.sh [output-directory]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$root/local-feed"
work="${1:-$(mktemp -d)}"
failures=0

# NuGet and pwsh are Windows processes under git-bash and do not understand /d/... paths.
native() {
    if command -v cygpath > /dev/null 2>&1; then cygpath -w "$1"; else printf %s "$1"; fi
}

log()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
pass() { printf '  \033[32mok\033[0m   %s\n' "$*"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }

log "Packing"
rm -rf "$feed"
dotnet pack "$root/src/FusionModules/FusionModules.csproj" -c Release -o "$feed" --nologo -v q
version="$(basename "$(ls "$feed"/FusionModules.*.nupkg | grep -v symbols | head -1)" .nupkg)"
version="${version#FusionModules.}"
echo "  FusionModules $version"

log "Package layout"
# pwsh rather than unzip: pwsh is on every CI image this runs on and in git-bash's PATH on
# Windows, and unzip is on neither.
nupkg="$(native "$feed/FusionModules.$version.nupkg")"
contents="$(pwsh -NoProfile -Command "[IO.Compression.ZipFile]::OpenRead('$nupkg').Entries.FullName" | tr -d '\r')"
for expected in \
    "analyzers/dotnet/cs/FusionModules.Analyzers.dll" \
    "analyzers/dotnet/cs/FusionModules.CodeFixes.dll" \
    "buildTransitive/FusionModules.targets" \
    "lib/net8.0/FusionModules.dll" \
    "lib/net10.0/FusionModules.dll"
do
    if grep -qx "$expected" <<<"$contents"; then pass "$expected"; else fail "missing $expected"; fi
done

# A scratch consumer outside the repository, so none of our own Directory.Build.props reaches it.
rm -rf "$work/consumer"
mkdir -p "$work/consumer/GoodModule" "$work/consumer/BadModule" "$work/consumer/Host" "$work/consumer/BadHost" "$work/consumer/NoPackage"

feed_for_nuget="$(native "$feed")"

cat > "$work/consumer/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed_for_nuget" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

write_project() {
    cat > "$1/$(basename "$1").csproj" <<XML
<Project Sdk="$2">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    $3
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
  </ItemGroup>
</Project>
XML
}

write_project "$work/consumer/GoodModule" "Microsoft.NET.Sdk" ""
cat > "$work/consumer/GoodModule/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using FusionModules;

[assembly: HostingStartup(typeof(Module))]

sealed class Module : ModuleBase { }

// Public on purpose, and allowed by name in .editorconfig below. Two things at once: it proves
// MOD0001's escape hatch survives packaging, and it gives BadHost a type to misuse for MOD0003.
public class Contract
{
    public static string Name => "contract";
}
CS
cat > "$work/consumer/GoodModule/.editorconfig" <<'INI'
root = true

[*.cs]
fusion_modules_allowed_public_types = Contract
INI

# Every module-side rule in one project. One build, one log, one grep per rule — a rule that
# stops firing when the package is rebuilt has nowhere to hide.
write_project "$work/consumer/BadModule" "Microsoft.NET.Sdk" ""
cat > "$work/consumer/BadModule/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FusionModules;

[assembly: HostingStartup(typeof(Module))]
[assembly: HostingStartup(typeof(NotAModule))]   // MOD0002

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter>(this);   // MOD0007
        services.AddHostedService<Worker>();           // MOD0008
    }

    public static void Replace(IWebHostBuilder builder) =>
        builder.Configure(app => { });                 // MOD0006
}

public class Leaked { }                                // MOD0001

sealed class Forgotten : ModuleBase { }                // MOD0005

sealed class NotAModule { }

sealed class Worker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

interface IPaymentClient                               // MOD0009
{
    Task Paid(string reference);
}

sealed class PaymentHub : Hub<IPaymentClient> { }
CS

# MOD0003 and MOD0004 belong to the host, so they need a host that gets them wrong.
cat > "$work/consumer/BadHost/BadHost.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
    <ProjectReference Include="../GoodModule/GoodModule.csproj" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/BadHost/Program.cs" <<'CS'
using Microsoft.AspNetCore.Mvc.ApplicationParts;

[assembly: ApplicationPart("GoodModule")]   // MOD0004

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Contract.Name);       // MOD0003
await app.RunAsync();
CS

# MOD0020 says the runtime package is missing, so the project has to have the analyzer without it.
# ExcludeAssets=compile leaves the analyzer and the MSBuild assets and removes ModuleBase.
cat > "$work/consumer/NoPackage/NoPackage.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="FusionModules" Version="$version" ExcludeAssets="compile" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/NoPackage/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;

[assembly: HostingStartup(typeof(Module))]   // MOD0020

sealed class Module { }
CS

write_project "$work/consumer/Host" "Microsoft.NET.Sdk.Web" "<OutputType>Exe</OutputType>"
cat > "$work/consumer/Host/Program.cs" <<'CS'
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseRouting();
await app.RunAsync();
CS

log "A well-formed module builds clean"
if dotnet build "$work/consumer/GoodModule" --nologo -v q > "$work/good.log" 2>&1; then
    pass "builds"
    if grep -q "MOD" "$work/good.log"; then fail "unexpected diagnostics:"; grep "MOD" "$work/good.log"; else pass "no diagnostics"; fi
else
    fail "did not build"; cat "$work/good.log"
fi

# Severity is part of a rule's contract — an error people must deal with, or a warning they may
# decide about — and it is carried by the package's own AnalyzerReleases file.
expect() {
    if grep -q "$2 $3" "$1"; then pass "$3 reported as $2"; else fail "$3 not reported as $2"; fi
}

log "Rules fire from the packaged analyzer"
dotnet build "$work/consumer/BadModule" --nologo -v q > "$work/bad.log" 2>&1 || true
for rule in MOD0001 MOD0002 MOD0005 MOD0006 MOD0009; do
    expect "$work/bad.log" error "$rule"
done
for rule in MOD0007 MOD0008; do
    expect "$work/bad.log" warning "$rule"
done

dotnet build "$work/consumer/BadHost" --nologo -v q > "$work/badhost.log" 2>&1 || true
for rule in MOD0003 MOD0004; do
    expect "$work/badhost.log" error "$rule"
done

dotnet build "$work/consumer/NoPackage" --nologo -v q > "$work/nopackage.log" 2>&1 || true
expect "$work/nopackage.log" warning MOD0020

log "Help links reach the documentation"
if grep -q "docs/rules/MOD0001.md" "$work/bad.log"; then pass "help link present"; else fail "no help link"; fi

log "net8.0 consumers restore and build"
# Only that the package is consumable on the older target framework. Whether a module actually
# activates at runtime on net8 is asserted by samples/01-minimal-api/Tests, which multi-targets —
# a proper test, rather than a background process and a polled port in shell.
mkdir -p "$work/consumer/Net8Module"
sed 's|net10.0|net8.0|' <<XML > "$work/consumer/Net8Module/Net8Module.csproj"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/Net8Module/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using FusionModules;

[assembly: HostingStartup(typeof(Module))]

sealed class Module : ModuleBase { }
CS
if dotnet build "$work/consumer/Net8Module" --nologo -v q > "$work/net8.log" 2>&1; then
    pass "net8.0 module builds"
else
    fail "net8.0 did not build"; cat "$work/net8.log"
fi

log "A host builds clean"
if dotnet build "$work/consumer/Host" --nologo -v q > "$work/host.log" 2>&1; then
    pass "builds"
else
    fail "did not build"; cat "$work/host.log"
fi

# The NuGet-generated imports that carry buildTransitive assets only exist after restore,
# so every project queried below has to have been built first.
log "buildTransitive assets apply"
kind="$(dotnet msbuild "$work/consumer/Host" -getProperty:FusionModulesProjectKind -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$kind" = "Host" ] && pass "host detected as FusionModulesProjectKind=Host" || fail "FusionModulesProjectKind was '$kind', expected 'Host'"

parts="$(dotnet msbuild "$work/consumer/Host" -getProperty:GenerateMvcApplicationPartsAssemblyAttributes -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$parts" = "false" ] && pass "Application Part Discovery disabled for the host" || fail "GenerateMvcApplicationPartsAssemblyAttributes was '$parts', expected 'false'"

kind="$(dotnet msbuild "$work/consumer/GoodModule" -getProperty:FusionModulesProjectKind -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$kind" = "Module" ] && pass "library detected as FusionModulesProjectKind=Module" || fail "FusionModulesProjectKind was '$kind', expected 'Module'"

# The implicit usings a module loses when it stops being a Microsoft.NET.Sdk.Web project.
usings="$(dotnet msbuild "$work/consumer/GoodModule" -getItem:Using -v:q 2>/dev/null)"
case "$usings" in
    *Microsoft.AspNetCore.Builder*) pass "module gets the Web SDK's implicit usings" ;;
    *) fail "no Microsoft.AspNetCore.Builder in @(Using) for a module" ;;
esac

# And the analyzers it loses with them: these live in the Web SDK's own folder, not in the
# framework reference, so nothing else puts MVC1000 or ASP0000 back.
analyzers="$(dotnet msbuild "$work/consumer/GoodModule" -getItem:Analyzer -v:q 2>/dev/null)"
case "$analyzers" in
    *Microsoft.AspNetCore.Mvc.Analyzers.dll*) pass "module gets the Web SDK's MVC analyzers" ;;
    *) fail "no Microsoft.AspNetCore.Mvc.Analyzers.dll in @(Analyzer) for a module" ;;
esac
case "$analyzers" in
    *Microsoft.AspNetCore.Analyzers.dll*) pass "module gets the Web SDK's Startup analyzer" ;;
    *) fail "no Microsoft.AspNetCore.Analyzers.dll in @(Analyzer) for a module" ;;
esac

log "Result"
if [ "$failures" -eq 0 ]; then
    echo "  all checks passed"
else
    echo "  $failures check(s) failed"
    exit 1
fi
