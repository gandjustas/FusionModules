#!/usr/bin/env bash
#
# Builds the packages and consumes them the way a stranger would.
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
# Every package, one version: MinVer reads it from the same tag.
for project in FusionModules FusionModules.EntityFrameworkCore FusionModules.Testing; do
    dotnet pack "$root/src/$project/$project.csproj" -c Release -o "$feed" --nologo -v q
done
# FusionModules.<digit>: FusionModules.* alone also matches FusionModules.EntityFrameworkCore and
# FusionModules.Testing.
version="$(basename "$(ls "$feed"/FusionModules.[0-9]*.nupkg | grep -v symbols | head -1)" .nupkg)"
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

# A package that ships beside FusionModules: its assemblies for both target frameworks, and its
# FusionModules the one packed with it — a project reference becomes a dependency on the same version.
# Its one other dependency has to be at the floor the project names, per target framework, not at the
# patch Directory.Packages.props pins for the tests: a pinned floor raised a consumer on EF 10.0.7 to
# EF Core 10.0.14 beside its own 10.0.7 provider.
check_companion() {
    local id="$1" dependency="$2" net8="$3" net10="$4" path entries nuspec groups tfm floor
    path="$feed/$id.$version.nupkg"
    if [ ! -f "$path" ]; then
        fail "no $id.$version.nupkg: the packages were packed at different versions"
        return
    fi
    path="$(native "$path")"
    entries="$(pwsh -NoProfile -Command "[IO.Compression.ZipFile]::OpenRead('$path').Entries.FullName" | tr -d '\r')"
    for expected in "lib/net8.0/$id.dll" "lib/net10.0/$id.dll"; do
        if grep -qx "$expected" <<<"$entries"; then pass "$id: $expected"; else fail "$id: missing $expected"; fi
    done
    nuspec="$(pwsh -NoProfile -Command "\$z = [IO.Compression.ZipFile]::OpenRead('$path'); \$e = \$z.GetEntry('$id.nuspec'); (New-Object IO.StreamReader(\$e.Open())).ReadToEnd()" | tr -d '\r')"
    if grep -q "id=\"FusionModules\" version=\"$version\"" <<<"$nuspec"; then
        pass "$id depends on FusionModules $version"
    else
        fail "$id does not depend on FusionModules $version"; grep -i "dependency" <<<"$nuspec" || true
    fi
    # One line per <group>, so a dependency is read with the target framework it belongs to.
    groups="$(tr -d '\n' <<<"$nuspec" | sed 's/<group /\n<group /g')"
    for tfm in net8.0 net10.0; do
        [ "$tfm" = net8.0 ] && floor="$net8" || floor="$net10"
        if grep "targetFramework=\"$tfm\"" <<<"$groups" | grep -q "id=\"$dependency\" version=\"$floor\""; then
            pass "$id depends on $dependency >= $floor on $tfm"
        else
            fail "$id does not depend on $dependency >= $floor on $tfm:"; grep "targetFramework=\"$tfm\"" <<<"$groups" | grep -o "<dependency [^>]*>" || true
        fi
    done
}

# The floors: the lowest versions each package supports (see the two csproj files for why EF Core 8's
# is 8.0.10).
check_companion FusionModules.EntityFrameworkCore Microsoft.EntityFrameworkCore 8.0.10 10.0.0
check_companion FusionModules.Testing Microsoft.AspNetCore.Mvc.Testing 8.0.0 10.0.0

# A stranger does not have our global packages folder, and that is not a detail here: NuGet serves
# a PackageReference out of it whenever that id and version are already extracted there, and never
# looks at the feed. On a release tag the version just packed is the version already on nuget.org,
# so everything below would restore the published package and pass — measured, on v0.1.0, against a
# targets change the consumer never saw. An empty packages folder per run leaves the feed as the
# only place the package can come from.
#
# Set after packing on purpose: the build above restores this repository's own dependencies, and
# they have no reason to be downloaded again.
export NUGET_PACKAGES="$(native "$work/packages")"

# A scratch consumer outside the repository, so none of our own Directory.Build.props reaches it.
rm -rf "$work/consumer"
mkdir -p "$work/consumer/GoodModule" "$work/consumer/BadModule" "$work/consumer/Host" "$work/consumer/BadHost" "$work/consumer/BadLibrary" "$work/consumer/NoModules" "$work/consumer/NoPackage"

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
// MOD0001's escape hatch survives packaging, and it gives BadHost a type to misuse for MOD0004.
public class Contract
{
    public static string Name => "contract";
}

// A type in a namespace every web host imports. Without /doc the compiler counts the module as
// used by any host that imports it; MOD0004 must not take that for use.
namespace Microsoft.Extensions.Hosting
{
    internal static class GoodModuleHostingExtensions { }
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
    protected override void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IStartupFilter>(this);   // MOD0009
    }

    public static void Replace(IWebHostBuilder builder) =>
        builder.Configure(app => { });                 // MOD0008
}

public class Leaked { }                                // MOD0001

sealed class Forgotten : ModuleBase { }                // MOD0003

sealed class NotAModule { }

interface IPaymentClient                               // MOD0010
{
    Task Paid(string reference);
}

sealed class PaymentHub : Hub<IPaymentClient> { }

// Public (MOD0001), and the module never adds its assembly as an application part (MOD0011).
public sealed class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase { }
CS

# MOD0004 and MOD0007 belong to the host, so they need a host that gets them wrong.
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

[assembly: ApplicationPart("GoodModule")]   // MOD0007

var builder = WebApplication.CreateBuilder(args);
_ = WebApplication.CreateSlimBuilder(args); // MOD0006
var app = builder.Build();
app.MapGet("/", () => Contract.Name);       // MOD0004
await app.RunAsync();
CS

# MOD0005: a plain library that uses a module's type takes the module wherever it goes.
cat > "$work/consumer/BadLibrary/BadLibrary.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
    <ProjectReference Include="../GoodModule/GoodModule.csproj" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/BadLibrary/Quotes.cs" <<'CS'
public static class Quotes
{
    public static string For() => Contract.Name;   // MOD0005
}
CS

# The package added to a web project with no module yet: nothing to validate, nothing to say.
cat > "$work/consumer/NoModules/NoModules.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/NoModules/Program.cs" <<'CS'
var app = WebApplication.CreateBuilder(args).Build();
_ = WebApplication.CreateSlimBuilder(args);   // no module to load: no MOD0006
await app.RunAsync();
CS

# MOD0013 says the runtime package is missing, so the project has to have the analyzer without it.
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

[assembly: HostingStartup(typeof(Module))]   // MOD0013

sealed class Module { }
CS

# MOD0014: two modules register one contract with different implementations, and the host
# references both. Half of the rule runs in each module's build (the generator records what it
# registers) and half in the host's, so it is only proved across packaged, separately built projects.
mkdir -p "$work/consumer/Checks" "$work/consumer/OrdersChecks" "$work/consumer/BillingChecks" "$work/consumer/CollidingHost"
cat > "$work/consumer/Checks/Checks.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
XML
cat > "$work/consumer/Checks/IChecker.cs" <<'CS'
namespace Checks;

public interface IChecker
{
    bool Check(string value);
}
CS
for name in Orders Billing; do
    cat > "$work/consumer/${name}Checks/${name}Checks.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
    <ProjectReference Include="../Checks/Checks.csproj" />
  </ItemGroup>
</Project>
XML
    cat > "$work/consumer/${name}Checks/Module.cs" <<CS
using Checks;
using FusionModules;

[assembly: HostingStartup(typeof(${name}Module))]

sealed class ${name}Module : ModuleBase
{
    protected override void ConfigureServices(IHostApplicationBuilder builder) =>
        builder.Services.AddSingleton<IChecker, ${name}Checker>();
}

sealed class ${name}Checker : IChecker
{
    public bool Check(string value) => value.Length > 0;
}
CS
done
cat > "$work/consumer/CollidingHost/CollidingHost.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
    <ProjectReference Include="../OrdersChecks/OrdersChecks.csproj" />
    <ProjectReference Include="../BillingChecks/BillingChecks.csproj" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/CollidingHost/Program.cs" <<'CS'
var app = WebApplication.CreateBuilder(args).ValidateModules().Build();
await app.RunAsync();
CS

# A host as a host really is: referencing its module, which is also what gives the generated
# module list something to name.
cat > "$work/consumer/Host/Host.csproj" <<XML
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
cat > "$work/consumer/Host/Program.cs" <<'CS'
using FusionModules;

// ValidateModules too compiles only if the generator ran.
var builder = WebApplication.CreateBuilder(args).ValidateModules();
var app = builder.Build();
app.UseRouting();

// Compiles only if the generator ran: the host never declares this type.
app.MapGet("/modules", () => KnownModules.All);

await app.RunAsync();
CS

# What a host's data library looks like with FusionModules.EntityFrameworkCore: a context composed
# from the loaded modules, registered with UseModules, and the same for dotnet ef by name. It has
# to restore both packages from the feed, at the version just packed, and compile against both.
mkdir -p "$work/consumer/EfData"
cat > "$work/consumer/EfData/EfData.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="FusionModules" Version="$version" />
    <PackageReference Include="FusionModules.EntityFrameworkCore" Version="$version" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/EfData/ShopData.cs" <<'CS'
using FusionModules.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EfData;

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyModuleConfigurations(this);
}

public static class ShopDataExtensions
{
    public static IServiceCollection AddShopData(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddDbContext<ShopContext>(options => options.UseModules(configuration))
            .AddTransient<DbContext>(provider => provider.GetRequiredService<ShopContext>());

    /// <summary>For dotnet ef and tests: the modules by name.</summary>
    public static DbContextOptions<ShopContext> DesignTime(params string[] modules) =>
        new DbContextOptionsBuilder<ShopContext>().UseModules(modules).Options;
}
CS

# A topology test through FusionModules.Testing, with nothing else: Microsoft.AspNetCore.Mvc.Testing
# has to come with the package, and so do its buildTransitive targets, which copy the host's
# .deps.json here — without them WebApplicationFactory cannot start the host at all. So this one is
# run, not only built. No test framework either: the package has to need none.
mkdir -p "$work/consumer/HostTests"
cat > "$work/consumer/HostTests/HostTests.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FusionModules" Version="$version" />
    <PackageReference Include="FusionModules.Testing" Version="$version" />
    <ProjectReference Include="../Host/Host.csproj" />
  </ItemGroup>
</Project>
XML
cat > "$work/consumer/HostTests/Topology.cs" <<'CS'
using FusionModules.Testing;
using Microsoft.AspNetCore.Hosting;

sealed class GoodTopology() : ModularWebApplicationFactory<Program>("GoodModule")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseConnectionStrings(("Default", "Host=127.0.0.1;Port=1")).WithoutApplicationHostedServices();
        base.ConfigureWebHost(builder);
    }
}

static class Check
{
    static int Main()
    {
        using var factory = new GoodTopology();
        foreach (var route in factory.Services.Routes())
        {
            Console.WriteLine(route);
        }

        return 0;
    }
}
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
for rule in MOD0001 MOD0002 MOD0003 MOD0008 MOD0009 MOD0010 MOD0011; do
    expect "$work/bad.log" error "$rule"
done

dotnet build "$work/consumer/BadHost" --nologo -v q > "$work/badhost.log" 2>&1 || true
expect "$work/badhost.log" warning MOD0012
for rule in MOD0004 MOD0007 MOD0006; do
    expect "$work/badhost.log" error "$rule"
done

dotnet build "$work/consumer/BadLibrary" --nologo -v q > "$work/badlibrary.log" 2>&1 || true
expect "$work/badlibrary.log" error MOD0005

dotnet build "$work/consumer/NoPackage" --nologo -v q > "$work/nopackage.log" 2>&1 || true
expect "$work/nopackage.log" warning MOD0013

dotnet build "$work/consumer/CollidingHost" --nologo -v q > "$work/collidinghost.log" 2>&1 || true
expect "$work/collidinghost.log" warning MOD0014
if grep -q "MOD0014.*'Checks.IChecker'.*BillingChecks: BillingChecker; OrdersChecks: OrdersChecker" "$work/collidinghost.log"; then
    pass "MOD0014 names the service and both modules' implementations"
else
    fail "MOD0014's message does not name Checks.IChecker and both implementations:"; grep "MOD0014" "$work/collidinghost.log" || true
fi

log "A context composed from the modules, through FusionModules.EntityFrameworkCore"
if dotnet build "$work/consumer/EfData" --nologo -v q > "$work/efdata.log" 2>&1; then
    pass "builds against both packages"
    if grep -q "MOD0" "$work/efdata.log"; then fail "unexpected diagnostics:"; grep "MOD0" "$work/efdata.log"; else pass "no diagnostics"; fi
else
    fail "did not build"; cat "$work/efdata.log"
fi

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

log "A web project without modules builds clean"
if dotnet build "$work/consumer/NoModules" --nologo -v q > "$work/nomodules.log" 2>&1; then
    pass "builds"
    if grep -q "MOD" "$work/nomodules.log"; then fail "unexpected diagnostics:"; grep "MOD" "$work/nomodules.log"; else pass "no diagnostics"; fi
else
    fail "did not build"; cat "$work/nomodules.log"
fi

log "A host builds clean"
if dotnet build "$work/consumer/Host" --nologo -v q -p:EmitCompilerGeneratedFiles=true > "$work/host.log" 2>&1; then
    pass "builds"
    if grep -q "MOD0004" "$work/host.log"; then fail "MOD0004 for an imported namespace:"; grep "MOD0004" "$work/host.log"; else pass "no MOD0004 for an imported namespace"; fi
else
    fail "did not build"; cat "$work/host.log"
fi

# Program.cs above would not have compiled if the generator had not run, but a passing build says
# nothing about what the list holds — and an empty list is the shape of the failure that matters.
log "The module list is generated"
known="$(find "$work/consumer/Host/obj" -name 'FusionModules.KnownModules.g.cs' | head -1)"
if [ -n "$known" ]; then
    pass "KnownModules generated"
    if grep -q 'All = "GoodModule";' "$known"; then
        pass "names the referenced module"
    else
        fail "the generated list is not GoodModule:"; grep 'All = ' "$known" || true
    fi
else
    fail "no FusionModules.KnownModules.g.cs under the host's obj"
fi

# After the host's own build above: this one builds the host too, and an up-to-date host compiles
# nothing, so the generated files that step looks for would not be emitted.
log "A topology test through FusionModules.Testing"
if dotnet build "$work/consumer/HostTests" --nologo -v q > "$work/hosttests.log" 2>&1; then
    pass "builds against FusionModules.Testing"
    if grep -q "MOD0" "$work/hosttests.log"; then fail "unexpected diagnostics:"; grep "MOD0" "$work/hosttests.log"; else pass "no diagnostics"; fi
    # From its output folder, as a test runner starts it: WebApplicationFactory finds the host's
    # content root through MvcTestingAppManifest.json in the working directory.
    if (cd "$work/consumer/HostTests/bin/Debug/net10.0" && dotnet HostTests.dll) > "$work/hosttests-run.log" 2>&1; then
        pass "starts the host with GoodModule"
        if grep -q "^GET /modules (host)" "$work/hosttests-run.log"; then
            pass "Routes() names the host's route and its owner"
        else
            fail "Routes() did not report GET /modules (host):"; cat "$work/hosttests-run.log"
        fi
    else
        fail "did not start the host"; cat "$work/hosttests-run.log"
    fi
else
    fail "did not build"; cat "$work/hosttests.log"
fi

# The NuGet-generated imports that carry buildTransitive assets only exist after restore,
# so every project queried below has to have been built first.
log "buildTransitive assets apply"
# One evaluation per project: given more than one -getProperty or -getItem, dotnet msbuild answers
# them all at once, as JSON. Each call is a process start and an evaluation, so asking one thing
# at a time paid for both again and again.
host="$(dotnet msbuild "$work/consumer/Host" -getProperty:FusionModulesProjectKind -getProperty:GenerateMvcApplicationPartsAssemblyAttributes -v:q 2>/dev/null | tr -d '\r')"
module="$(dotnet msbuild "$work/consumer/GoodModule" -getProperty:FusionModulesProjectKind -getItem:Using -getItem:Analyzer -v:q 2>/dev/null | tr -d '\r')"

# A property's value out of that JSON. No jq: it is on neither CI image's PATH by default on Windows.
property() { grep -o "\"$2\": *\"[^\"]*\"" <<<"$1" | head -1 | sed 's/.*: *"\(.*\)"/\1/'; }

kind="$(property "$host" FusionModulesProjectKind)"
[ "$kind" = "Host" ] && pass "host detected as FusionModulesProjectKind=Host" || fail "FusionModulesProjectKind was '$kind', expected 'Host'"

parts="$(property "$host" GenerateMvcApplicationPartsAssemblyAttributes)"
[ "$parts" = "false" ] && pass "Application Part Discovery disabled for the host" || fail "GenerateMvcApplicationPartsAssemblyAttributes was '$parts', expected 'false'"

kind="$(property "$module" FusionModulesProjectKind)"
[ "$kind" = "Module" ] && pass "library detected as FusionModulesProjectKind=Module" || fail "FusionModulesProjectKind was '$kind', expected 'Module'"

# The implicit usings a module loses when it stops being a Microsoft.NET.Sdk.Web project.
case "$module" in
    *'"Microsoft.AspNetCore.Builder"'*) pass "module gets the Web SDK's implicit usings" ;;
    *) fail "no Microsoft.AspNetCore.Builder in @(Using) for a module" ;;
esac

# And the analyzers it loses with them: these live in the Web SDK's own folder, not in the
# framework reference, so nothing else puts MVC1000 or ASP0000 back.
case "$module" in
    *Microsoft.AspNetCore.Mvc.Analyzers.dll*) pass "module gets the Web SDK's MVC analyzers" ;;
    *) fail "no Microsoft.AspNetCore.Mvc.Analyzers.dll in @(Analyzer) for a module" ;;
esac
case "$module" in
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
