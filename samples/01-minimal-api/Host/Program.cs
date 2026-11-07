// The entire host. It adds routing and nothing else: which modules are live is decided by
// HOSTINGSTARTUPASSEMBLIES at startup, not by anything written here.
//
//   dotnet run                                          -> just /
//   HOSTINGSTARTUPASSEMBLIES=WeatherModule dotnet run    -> / and /weather
//
// See Properties/launchSettings.json for the same thing as launch profiles.

// ValidateModules is generated from this project's references: it fails startup when
// HOSTINGSTARTUPASSEMBLIES names something that did not load, or a module whose startup did not
// run. Left alone, ASP.NET Core logs a critical message and starts anyway.
var builder = WebApplication.CreateBuilder(args).ValidateModules();

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
