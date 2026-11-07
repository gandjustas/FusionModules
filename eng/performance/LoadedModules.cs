#:project ../../src/FusionModules/FusionModules.csproj
#:project ../../src/FusionModules.EntityFrameworkCore/FusionModules.EntityFrameworkCore.csproj
#:package Microsoft.EntityFrameworkCore.Sqlite
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
// What GetLoadedModules and UseModules cost per call. AddDbContext builds a context's options per
// scope - per request - so this is paid on every one.
//
//   dotnet run eng/performance/LoadedModules.cs -c Release
//
// A .NET 10 file-based program: no project, the #: lines above are its project. The package's
// version is the one Directory.Packages.props pins for net10.0.
using System.Diagnostics;
using FusionModules;
using FusionModules.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Ten assemblies stand for ten modules: GetLoadedModules reads names, types and references only.
// HOSTINGSTARTUPASSEMBLIES names all ten, so none of them is taken for the host's own assembly and
// the order is the dependency order - what a host whose project is not itself a module sees.
var types = new[] { typeof(DbContext), typeof(ModuleBase), typeof(IConfiguration), typeof(Microsoft.Data.Sqlite.SqliteConnection),
    typeof(System.Text.Json.JsonDocument), typeof(System.Net.Http.HttpClient), typeof(System.Xml.XmlDocument),
    typeof(System.Linq.Enumerable), typeof(System.Collections.Concurrent.ConcurrentBag<int>), typeof(Microsoft.Extensions.Logging.ILogger) };
var registry = new Dictionary<string, string?> { ["HOSTINGSTARTUPASSEMBLIES"] = string.Join(';', types.Select(t => t.Assembly.GetName().Name)) };
foreach (var t in types) registry[$"FusionModules:Modules:{t.Assembly.GetName().Name}"] = t.AssemblyQualifiedName;
var configuration = new ConfigurationBuilder().AddInMemoryCollection(registry).Build();

if (ModuleBase.GetLoadedModules(configuration).Count != types.Length)
{
    throw new InvalidOperationException("Every stand-in module should resolve.");
}

double Time(string name, int n, Action action)
{
    // At least 2,000 calls and half a second: tiered compilation promotes a method only after a
    // delay, and 2,000 calls of a 4 us method end before it - the first row then measured tier-0
    // code and read three times slower than the same call timed last.
    var warm = Stopwatch.StartNew();
    for (var i = 0; i < 2000 || warm.ElapsedMilliseconds < 500; i++) action();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < n; i++) action();
    var us = sw.Elapsed.TotalMilliseconds * 1000 / n;
    Console.WriteLine($"{name,-55} {us,8:F2} us");
    return us;
}

const int N = 50_000;
Time("GetLoadedModules (10 modules)", N, () => ModuleBase.GetLoadedModules(configuration));
Time("  section.GetChildren()", N, () => configuration.GetSection("FusionModules:Modules").GetChildren().ToList());
Time("  Type.GetType x10", N, () => { foreach (var t in types) Type.GetType(t.AssemblyQualifiedName!); });
Time("EF options: UseSqlite", N, () => new DbContextOptionsBuilder<Shop>().UseSqlite("DataSource=:memory:").Options.ToString());
Time("EF options: UseSqlite + UseModules(configuration)", N, () => new DbContextOptionsBuilder<Shop>().UseSqlite("DataSource=:memory:").UseModules(configuration).Options.ToString());
Time("new Shop(options) + dispose (options prebuilt)", N, () => { using var c = new Shop(new DbContextOptionsBuilder<Shop>().UseSqlite("DataSource=:memory:").Options); });

sealed class Shop(DbContextOptions<Shop> options) : DbContext(options);
