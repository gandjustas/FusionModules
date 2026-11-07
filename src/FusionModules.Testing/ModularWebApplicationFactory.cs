using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FusionModules.Testing;

/// <summary>
/// Boots the host with a chosen set of modules: the same choice a deployment makes through
/// <c>HOSTINGSTARTUPASSEMBLIES</c>, so a test topology and a production topology are the same thing.
/// </summary>
/// <typeparam name="TEntryPoint">A type in the host's assembly, usually its <c>Program</c>.</typeparam>
/// <remarks>
/// <para>
/// The module names are assembly names, spelled as a deployment spells them. A misspelt one fails
/// startup when the host calls the generated <c>ValidateModules()</c>, and starts a host without that
/// module when it does not — which is what the same typo does in production.
/// </para>
/// <para>
/// Not sealed: a topology that needs more than its module names — a setting, a test service, a
/// handler pointing a client at another in-process host — overrides <see cref="ConfigureWebHost"/>
/// and calls the base. <see cref="ModularWebHostBuilderExtensions"/> has the two most topology tests
/// want.
/// </para>
/// </remarks>
public class ModularWebApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>Creates a factory for the host with these modules and no others.</summary>
    /// <param name="modules">
    /// Module assembly names, as <c>HOSTINGSTARTUPASSEMBLIES</c> takes them. None at all is a
    /// topology too: the host alone.
    /// </param>
    public ModularWebApplicationFactory(params string[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = [.. modules];
    }

    /// <summary>The module assembly names this host is started with.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>
    /// Sets <c>HOSTINGSTARTUPASSEMBLIES</c> to <see cref="Modules"/>, so the topology under test is the
    /// one the test names; fails when the test process's environment names modules too.
    /// </summary>
    /// <param name="builder">The host's web host builder.</param>
    /// <remarks>
    /// <c>ASPNETCORE_HOSTINGSTARTUPASSEMBLIES</c> in the environment of the test process cannot be
    /// replaced from here: the web host <c>WebApplicationFactory</c> wraps around the application reads
    /// its hosting startups from the environment ahead of any setting, and runs the environment's
    /// modules a second time, beside the ones the test named — the same module twice maps its
    /// endpoints twice. A developer's shell left with the variable set would then run every topology
    /// test against another topology, passing or failing for the wrong reason; so the factory refuses
    /// instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The environment names modules.</exception>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var modules = string.Join(';', Modules);
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} is set in the test process to '{fromEnvironment}', and ASP.NET Core loads the modules it names " +
                $"whatever the factory asks for ('{modules}'). Unset it for the test run: each test names its topology itself.");
        }

        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, modules);
        base.ConfigureWebHost(builder);
    }

    private const string EnvironmentVariable = "ASPNETCORE_" + "HOSTINGSTARTUPASSEMBLIES";
}
