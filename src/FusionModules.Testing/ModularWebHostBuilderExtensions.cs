using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Testing;

/// <summary>
/// What a topology test usually adds to the host: connection strings nothing answers, and none of
/// the modules' workers.
/// </summary>
/// <remarks>
/// A topology test asks what composed — which modules, routes and model — not what connected. Both
/// of these are opt-in, called from an override of
/// <see cref="ModularWebApplicationFactory{TEntryPoint}"/>'s <c>ConfigureWebHost</c>, because a host
/// whose modules touch no infrastructure needs neither.
/// </remarks>
public static class ModularWebHostBuilderExtensions
{
    /// <summary>
    /// Each named connection string set to the value given — for a topology test, one that parses
    /// and that nothing answers, so that the host starts without opening a connection.
    /// </summary>
    /// <param name="builder">The host's web host builder.</param>
    /// <param name="connectionStrings">
    /// The names under <c>ConnectionStrings</c> the host reads, such as <c>Postgres</c>, each with the
    /// connection string to use, written in its client's format:
    /// <c>("Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")</c>,
    /// <c>("RabbitMQ", "host=127.0.0.1;port=1")</c>.
    /// </param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// <para>
    /// The connection strings are present so that a module which requires one finds it, and should
    /// point at an address nothing listens on — port 1 on the loopback address — so that a test which
    /// does connect fails at once rather than finding a server somebody left running. Building an
    /// EF Core model needs a provider, not a connection, and a client that connects lazily never
    /// notices. The value is the caller's because no one string parses for every client: one in
    /// Npgsql's keyword form, given to EasyNetQ, failed only on the first use of the bus. docs/testing.md
    /// has one for Npgsql, SQL Server, RabbitMQ and Redis.
    /// </para>
    /// <para>
    /// Work the host does at startup — migrating, seeding — is the host's to switch off, with its own
    /// key: <c>builder.UseSetting("Database:Migrate", "false")</c> in the samples.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A name or a connection string is null or empty.</exception>
    public static IWebHostBuilder UseConnectionStrings(this IWebHostBuilder builder, params (string Name, string ConnectionString)[] connectionStrings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionStrings);

        foreach (var (name, connectionString) in connectionStrings)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException("Each connection string needs a name and a value.", nameof(connectionStrings));
            }
        }

        foreach (var (name, connectionString) in connectionStrings)
        {
            builder.UseSetting($"ConnectionStrings:{name}", connectionString);
        }

        return builder;
    }

    /// <summary>
    /// Removes every hosted service but the web host's own, so the modules' workers do not start.
    /// </summary>
    /// <param name="builder">The host's web host builder.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// <para>
    /// A module's <c>IHostedService</c> really runs inside <c>WebApplicationFactory</c>: a worker
    /// that loads translations from a database in <c>StartAsync</c>, or subscribes to a queue, fails
    /// or hangs every topology test on a machine that has none of that — for a reason that has
    /// nothing to do with composition.
    /// </para>
    /// <para>
    /// <c>GenericWebHostService</c> stays. It is what builds the pipeline, so the modules'
    /// <c>Configure</c>, their startup filters, the FusionModules startup checks and the route table
    /// a test came for all still happen. It is internal to ASP.NET Core, so it is matched by name.
    /// Work a host does itself between <c>Build()</c> and <c>RunAsync()</c> is not a hosted service
    /// and still runs.
    /// </para>
    /// </remarks>
    public static IWebHostBuilder WithoutApplicationHostedServices(this IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigureTestServices(services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IHostedService) && !IsWebHost(services[i]))
                {
                    services.RemoveAt(i);
                }
            }
        });
    }

    private static bool IsWebHost(ServiceDescriptor descriptor) =>
        !descriptor.IsKeyedService &&
        descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.Hosting.GenericWebHostService";
}
