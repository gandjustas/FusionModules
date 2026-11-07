// A host as small as a host gets: it checks HOSTINGSTARTUPASSEMBLIES and serves one route of its own.
// Which modules are live is the tests' choice, made through ModularWebApplicationFactory.
var builder = WebApplication.CreateBuilder(args).ValidateModules();

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

// A route whose handler the framework declares, as MapOpenApi's document is: no module's and not the
// host's, so neither the inventory nor the startup report gives it an owner.
app.MapGet("/framework/id", Guid.NewGuid);

// Static assets: wwwroot/site.css, which MapStaticAssets maps as several endpoints, each carrying a
// StaticAssetDescriptor. ASP.NET Core 8 has neither, so there one endpoint carries a type of that
// name — the inventory recognises it by name — with a handler that, like the real ones, is not the
// host's.
#if NET9_0_OR_GREATER
app.MapStaticAssets();
#else
app.MapGet("/site.css", Guid.NewGuid).WithMetadata(new Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor());
#endif

await app.RunAsync();

// WebApplicationFactory needs a type from this assembly; net8.0 does not make Program public by itself.
public partial class Program;

#if !NET9_0_OR_GREATER
namespace Microsoft.AspNetCore.StaticAssets
{
    /// <summary>Stands for ASP.NET Core 9's static asset metadata, matched by its full name.</summary>
    internal sealed class StaticAssetDescriptor;
}
#endif
