// MVC and Razor Pages modules need nothing extra from the host beyond routing and static assets.
// It still registers no controllers, no pages, no application parts and no module.
//
//   HOSTINGSTARTUPASSEMBLIES=DashboardModule dotnet run   -> /Dashboard
//   HOSTINGSTARTUPASSEMBLIES=StatusModule    dotnet run   -> /status

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();

// Serving what the modules put in wwwroot. MapStaticAssets — the endpoint-based one that reads the
// build-time manifest and adds fingerprinting and compression — arrived in .NET 9, so on net8 the
// host uses the middleware it replaced. Either way the manifest is built from project references
// rather than from HOSTINGSTARTUPASSEMBLIES, which is the asymmetry the tests assert.
#if NET9_0_OR_GREATER
app.MapStaticAssets();
#else
app.UseStaticFiles();
#endif

app.MapGet("/", () => "host");

await app.RunAsync();

public partial class Program;
