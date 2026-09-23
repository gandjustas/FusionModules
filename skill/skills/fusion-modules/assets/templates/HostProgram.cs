// The host. It knows nothing about any module, and that is the property to protect.
//
// Which modules run is decided by HOSTINGSTARTUPASSEMBLIES at startup. Nothing here changes
// between topologies, which is why one image can serve all of them.

var builder = WebApplication.CreateBuilder(args);

// Infrastructure the deployment owns rather than the application: connection strings, logging
// sinks, telemetry exporters. Not features — those belong in modules.

var app = builder.Build();

// Modules map their endpoints into the host's routing. WebApplication adds routing on its own if
// the host maps anything itself, and throws a message naming this call if neither does — but
// being explicit costs nothing and removes the question.
app.UseRouting();

// Only static assets if any module ships a wwwroot. They are served from _content/<AssemblyName>/.
app.MapStaticAssets();

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
