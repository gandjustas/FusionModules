// The host again knows nothing. It does not call AddSignalR and does not map a hub: both belong to
// whichever module has one, because a host that had to know would be back to naming its modules.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
