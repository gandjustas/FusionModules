using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FusionModules;

/// <summary>
/// The host's endpoints as the startup checks see them: read once, and each with the assembly that
/// serves it. The route check compares them; the startup report counts them per module.
/// </summary>
internal static partial class EndpointTable
{
    /// <summary>
    /// The host's endpoints, or null when they cannot be read — and then a warning saying why.
    /// </summary>
    /// <remarks>
    /// Reading the endpoints builds them — for a minimal API, its request delegate — which is otherwise
    /// done on the first request. A handler that cannot be built (a service the topology lacks, say)
    /// throws here, and must not stop a host that would have started without reading them: it would
    /// fail that request either way. So the reading stands down and says why.
    /// </remarks>
    public static IReadOnlyList<Endpoint>? Read(IServiceProvider services, ILogger logger)
    {
        if (services.GetService<EndpointDataSource>() is not { } source)
        {
            return null;
        }

        try
        {
            return source.Endpoints;
        }
        catch (Exception e)
        {
            logger.LogWarning(
                "Endpoints could not be read at startup: building them failed, and the first request will fail the same way. " +
                "Routes were not checked for collisions between modules, and the startup report counts no endpoints. {Reason}",
                e.Message);
            return null;
        }
    }
}
