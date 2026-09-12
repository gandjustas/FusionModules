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

log()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
pass() { printf '  \033[32mok\033[0m   %s\n' "$*"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }

log "Packing"
rm -rf "$feed"
dotnet pack "$root/src/Modulith/Modulith.csproj" -c Release -o "$feed" --nologo -v q
version="$(basename "$(ls "$feed"/Modulith.*.nupkg | grep -v symbols | head -1)" .nupkg)"
version="${version#Modulith.}"
echo "  Modulith $version"

log "Package layout"
contents="$(unzip -Z1 "$feed/Modulith.$version.nupkg")"
for expected in \
    "analyzers/dotnet/cs/Modulith.Analyzers.dll" \
    "analyzers/dotnet/cs/Modulith.CodeFixes.dll" \
    "buildTransitive/Modulith.targets" \
    "lib/net8.0/Modulith.dll" \
    "lib/net10.0/Modulith.dll"
do
    if grep -qx "$expected" <<<"$contents"; then pass "$expected"; else fail "missing $expected"; fi
done

# A scratch consumer outside the repository, so none of our own Directory.Build.props reaches it.
rm -rf "$work/consumer"
mkdir -p "$work/consumer/GoodModule" "$work/consumer/BadModule" "$work/consumer/Host"

# NuGet is a Windows process under git-bash and does not understand /d/... paths.
feed_for_nuget="$feed"
if command -v cygpath > /dev/null 2>&1; then
    feed_for_nuget="$(cygpath -w "$feed")"
fi

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
    <PackageReference Include="Modulith" Version="$version" />
  </ItemGroup>
</Project>
XML
}

write_project "$work/consumer/GoodModule" "Microsoft.NET.Sdk" ""
cat > "$work/consumer/GoodModule/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

sealed class Module : ModuleBase { }
CS

write_project "$work/consumer/BadModule" "Microsoft.NET.Sdk" ""
cat > "$work/consumer/BadModule/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

sealed class Module : ModuleBase { }

public class Leaked { }              // MOD0001

sealed class Forgotten : ModuleBase { }  // MOD0005
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

log "Rules fire from the packaged analyzer"
dotnet build "$work/consumer/BadModule" --nologo -v q > "$work/bad.log" 2>&1 || true
for rule in MOD0001 MOD0005; do
    if grep -q "error $rule" "$work/bad.log"; then pass "$rule reported"; else fail "$rule not reported"; fi
done

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
    <PackageReference Include="Modulith" Version="$version" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/Net8Module/Module.cs" <<'CS'
using Microsoft.AspNetCore.Hosting;
using Modulith;

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
kind="$(dotnet msbuild "$work/consumer/Host" -getProperty:ModulithProjectKind -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$kind" = "Host" ] && pass "host detected as ModulithProjectKind=Host" || fail "ModulithProjectKind was '$kind', expected 'Host'"

parts="$(dotnet msbuild "$work/consumer/Host" -getProperty:GenerateMvcApplicationPartsAssemblyAttributes -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$parts" = "false" ] && pass "Application Part Discovery disabled for the host" || fail "GenerateMvcApplicationPartsAssemblyAttributes was '$parts', expected 'false'"

kind="$(dotnet msbuild "$work/consumer/GoodModule" -getProperty:ModulithProjectKind -v:q 2>/dev/null | tr -d '\r\n ')"
[ "$kind" = "Module" ] && pass "library detected as ModulithProjectKind=Module" || fail "ModulithProjectKind was '$kind', expected 'Module'"

log "Result"
if [ "$failures" -eq 0 ]; then
    echo "  all checks passed"
else
    echo "  $failures check(s) failed"
    exit 1
fi
