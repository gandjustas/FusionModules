using Microsoft.EntityFrameworkCore;
using FusionModules.EntityFrameworkCore;

namespace ModularData.Host;

/// <summary>
/// The application's one <see cref="DbContext"/>, whose model is whatever the loaded modules say
/// it is.
/// </summary>
/// <remarks>
/// <para>
/// The modules come from the context's options — <c>UseModules</c> in <c>Program.cs</c>, from the
/// registry the modules write as they activate — and <c>ApplyModuleConfigurations</c> applies
/// their entity configurations in <c>ModuleBase.GetLoadedModules</c> order: the host's own module
/// first, when the host is one (this one is not), then each module after the modules it
/// references. BillingModule's relationship between orders and customers is applied after the two
/// entity modules it joins, wherever HOSTINGSTARTUPASSEMBLIES lists it.
/// </para>
/// <para>
/// Not <c>AppDomain.CurrentDomain.GetAssemblies()</c>: filtered by the attribute it does the same
/// job while one host owns the process, and stops the moment a second one shares it. In an
/// integration-test assembly every topology's modules are loaded, so every topology would compose
/// the union. Nothing throws; the tables are wrong.
/// </para>
/// </remarks>
internal sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyModuleConfigurations(this);
    }
}
