// Types the probe recognises by full name, standing in for the libraries that define them: the probe
// has no reference to EF Core or MassTransit either, and finds them by name in the application.

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext;
}

namespace Microsoft.EntityFrameworkCore.Internal
{
    public interface IDbContextPool<T>;
}

namespace MassTransit
{
    public sealed class ConfigurationException(string message) : Exception(message);
}
