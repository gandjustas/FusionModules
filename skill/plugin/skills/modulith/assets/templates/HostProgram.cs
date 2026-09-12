// The host. It knows nothing about any module, and that is the property to protect.
//
// Which modules run is decided by HOSTINGSTARTUPASSEMBLIES at startup. Nothing here changes
// between topologies, which is why one image can serve all of them.

var builder = WebApplication.CreateBuilder(args);

// Infrastructure the deployment owns rather than the application: connection strings, logging
// sinks, telemetry exporters. Not features — those belong in modules.

var app = builder.Build();

// Required. Modules map their endpoints into the host's routing; without this there is nothing
// for them to map into.
app.UseRouting();

// Only static assets if any module ships a wwwroot. They are served from _content/<AssemblyName>/.
app.MapStaticAssets();

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
