// MVC and Razor Pages modules need nothing extra from the host beyond routing and static assets.
// It still registers no controllers, no pages, no application parts and no module.
//
//   HOSTINGSTARTUPASSEMBLIES=DashboardModule dotnet run   -> /Dashboard
//   HOSTINGSTARTUPASSEMBLIES=StatusModule    dotnet run   -> /status

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();
app.MapStaticAssets();
app.MapGet("/", () => "host");

await app.RunAsync();

public partial class Program;
