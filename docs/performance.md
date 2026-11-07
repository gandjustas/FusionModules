# Performance: what the package costs a build and a request

## The build

The analyzers and generators run in every compilation of a project that references the package.
Two ways to see what they cost, and they answer different questions.

**What the compiler charges them.** `-p:ReportAnalyzer=true` makes the compiler report the time of
every analyzer and generator. It charges shared work — binding the references, building a semantic
model — to whichever component asks first, so a generator that runs before the compilation looks
more expensive than it is. Read it for which of the package's components matters, not for what the
build pays.

**What the build pays.** The same build with and without the package's analyzers, alternating, and
the compiler's own time (`Csc` in the performance summary) compared.

```bash
bash eng/performance/measure.sh samples/Samples.slnx 3
```

does both: a warm-up build (a cold one charges the analyzer assembly's load and JIT to the first
compilation), the report, parsed by `eng/performance/analyzer-times.py`, and `runs` pairs of builds
with and without, through `eng/performance/WithoutFusionModulesAnalyzers.targets`. Every build is
`--no-incremental -m:1`, so that the compiler's reports do not interleave. The script needs bash and
Python — git-bash on Windows — and reads the compiler's output in English or Russian. The report's
log can also be read on its own: `python eng/performance/analyzer-times.py <log> --detail` adds a
line per compilation.

Without the analyzers a project that uses what the generators write does not compile: every sample
host calls the generated `ValidateModules()`, and sample 03's design-time factory names
`KnownModules`. Those compilations fail early, and the projects that depend on them — the samples'
test projects — are never compiled at all.
The script prints the number of `Csc` calls and of error lines next to the time; when they differ
between the two columns, the times are not a comparison. For one, build a project whose
compilations do not use the generated code — a module and what it references.

### Measured on this branch

7 November 2026, at commit `6167f39` (0.3 in development): Windows 11, Intel Core i9-9900KF, 8 cores /
16 threads, 64 GB; .NET SDK 10.0.403. Warm, `--no-incremental -m:1`, Debug.

What the compiler charges, `samples/Samples.slnx` — 52 compilations, 46 of them with the package's
analyzers (the samples build `FusionModules` itself from source, for net8.0 and net10.0, with its own
analyzers):

| | All | FusionModules |
|---|---|---|
| Analyzers | 2.50 s | 0.117 s — 4.7 % |
| Generators | 2.09 s | 0.995 s — 48 % |

| FusionModules component | Charged, all 52 compilations |
|---|---|
| `ModuleRecordsGenerator` (the registrations MOD0014 and the startup check read) | 0.604 s |
| `KnownModulesGenerator` | 0.403 s |
| `HostAnalyzer` | 0.089 s |
| `ModuleAnalyzer`, `ControllerDiscoveryAnalyzer`, `HostValidationAnalyzer`, `SlimBuilderAnalyzer`, `ModuleUsageAnalyzer` | 0.023–0.025 s each |

For scale, `Microsoft.CodeAnalysis.CSharp.CodeStyle` is charged 0.80 s in the same build and TUnit's
generator 0.56 s. The most any one compilation is charged for the package's generators is 0.11 s
(`Orders.Entities`, the first module compiled); most are charged 0.02–0.07 s.

What the build pays. Over the whole samples solution, three pairs: `Csc` 5.9–6.7 s with the
analyzers in 52 calls, 3.9–5.3 s without in 41 calls with 58 error lines — the hosts do not compile
and eleven compilations never happen, as above, so the difference is not the analyzers' cost. Over
`samples/03-modular-data/Modules/Billing/BillingModule.csproj` instead — 10 compilations: Billing,
the two entity modules and `FusionModules`, each for net8.0 and net10.0, and the analyzer projects —
five pairs:

| `Csc`, ms | Pairs | Mean |
|---|---|---|
| With the package's analyzers | 1085, 1126, 1055, 1150, 1043 | 1092 |
| Without | 1014, 1036, 1171, 1067, 1027 | 1063 |

The compiler charges the package 0.20 s of that build (0.178 s to the generators); the build pays
about 30 ms on average, inside the noise of either column. The generators' charged time is mostly
reference binding the compilation does anyway.

The full run on the samples, three pairs, took about three minutes; the Billing run, five pairs,
about one.

### MOD0004's second binding

The one component that can do real extra work is [MOD0004](rules/MOD0004.md) in `HostAnalyzer`.
`GetUsedAssemblyReferences` without `/doc` counts an assembly as used when it merely contributes a
type to a namespace the host imports, so when that cheap answer names a module, the rule binds the
host a second time: syntax trees re-parsed with `DocumentationMode.Parse`, which switches import
tracking on, and documentation comments stripped, so that a `cref` does not count as use. That
second binding is the price of not reporting a module the host does not use, and it is paid only
when it could change the answer:

- a project that references no module returns before asking for used references at all — asking
  binds every method body;
- the second binding runs only when the cheap answer has already named a module, and a compilation
  with errors is not rebound.

In the samples' hosts the compiler charges `HostAnalyzer` 3–10 ms per compilation. A host with much
more code — and Razor's generated code is code — pays more for the cheap answer and, if a module
shows up in it, again for the second; measure such a host with the script above, one project at a
time.

Solutions outside this repository — the ones the package was first measured on during the 0.2.0
previews — are not measured here.

## A request

`AddDbContext` builds a context's options per scope, so `UseModules(configuration)` runs on every
request that creates a context.

```bash
dotnet run eng/performance/LoadedModules.cs -c Release
```

A .NET 10 file-based program: the `#:` lines at its top reference `FusionModules`,
`FusionModules.EntityFrameworkCore` and EF Core's SQLite provider, at the version
`Directory.Packages.props` pins for net10.0. Ten BCL and framework assemblies stand in for ten
modules, registered under `FusionModules:Modules:<name>` and all named in `HOSTINGSTARTUPASSEMBLIES`,
so none is taken for the host's own assembly. Each row is warmed up for at least 2,000 calls and
half a second, so that tiered compilation has promoted the code, then timed over 50,000 calls.

Measured as above, three runs:

| Per call, ten modules | |
|---|---|
| `GetLoadedModules` | 5.4–6.7 µs |
| … of which reading the registry, `GetChildren()` | 1.9 µs |
| … what resolving the ten types would cost without the cache, `Type.GetType` ×10 | 15.7–16.2 µs |
| EF Core options with `UseSqlite` alone | 4.9–5.1 µs |
| … with `UseModules(configuration)` as well | 11.0–11.1 µs |
| A context created from prebuilt options and disposed | 8.0–8.2 µs |

`GetLoadedModules` keeps its answer per registry — the registry as it stands is the key, so one
still being written by activating modules is never answered from a stale entry — and what a call
pays is reading the registry and building that key. `UseModules` keeps one options extension per
module set, so it adds `GetLoadedModules` and little else: about 6 µs a request.
