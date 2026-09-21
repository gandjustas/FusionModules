using Startup.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<StartupLog>();

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

// The gate. Between Build() and RunAsync() is the only place in the lifetime where the container
// exists and the server has not started, and a module cannot reach it — so the host does, over a
// contract it owns. It still knows nothing about modules: it enumerates StartupTask, and who
// registered one is none of its business.
//
// Ordering lives here too, which is the other reason not to push this into the modules. A gate
// that must precede everything cannot be ordered against other modules from inside one of them.
var log = app.Services.GetRequiredService<StartupLog>();

foreach (var task in app.Services.GetServices<StartupTask>())
{
    await using var scope = app.Services.CreateAsyncScope();
    await task.RunAsync(scope.ServiceProvider, CancellationToken.None);
    log.Record($"task:{task.Name}");
}

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
