// The entire host. It adds routing and nothing else: which modules are live is decided by
// HOSTINGSTARTUPASSEMBLIES at startup, not by anything written here.
//
//   dotnet run                                          -> just /
//   HOSTINGSTARTUPASSEMBLIES=WeatherModule dotnet run    -> / and /weather
//
// See Properties/launchSettings.json for the same thing as launch profiles.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
