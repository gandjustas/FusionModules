using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ModularData.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>();

    // A topology that loads a subset of the modules legitimately has a smaller model than the
    // migrations snapshot, and MigrateAsync treats that as an error by default. Downgraded to a
    // log entry rather than suppressed, because it still means something on the full model.
    //
    // EF Core 9 introduced the check and the warning; on EF 8 there is nothing to downgrade, and
    // the same topology simply migrates without comment.
#if NET9_0_OR_GREATER
    options.ConfigureWarnings(warnings => warnings.Log(RelationalEventId.PendingModelChangesWarning));
#endif
});

// Modules depend on DbContext, never on this application's context type.
builder.Services.AddTransient<DbContext>(services => services.GetRequiredService<ApplicationDbContext>());

var app = builder.Build();

if (app.Configuration.GetValue("Database:Migrate", defaultValue: true))
{
    // Migrations are generated against the union of all modules and applied whole. A topology
    // that loads a subset simply has tables it does not use; generating per-topology migrations
    // would mean a database whose shape depends on which replica reached it first.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
}

app.UseRouting();
app.MapGet("/", () => "host");

await app.RunAsync();

public partial class Program;
