// Types the probe recognises by full name, standing in for the libraries that define them: the probe
// has no reference to EF Core or MassTransit either, and finds them by name in the application.

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext;
}

namespace Microsoft.EntityFrameworkCore.Infrastructure
{
    public interface IInfrastructure<T>
    {
        T Instance { get; }
    }

    public enum MetadataDebugStringOptions
    {
        ShortDefault,
        LongDefault,
    }
}

namespace Microsoft.EntityFrameworkCore.Metadata
{
    using Microsoft.EntityFrameworkCore.Infrastructure;

    public interface IDesignTimeModel
    {
        IReadOnlyModel Model { get; }
    }

    public interface IReadOnlyModel
    {
        string ToDebugString(MetadataDebugStringOptions options, int indent);

        IEnumerable<IReadOnlyEntityType> GetEntityTypes();
    }

    public interface IReadOnlyTypeBase
    {
        string Name { get; }
    }

    public interface IReadOnlyEntityType : IReadOnlyTypeBase
    {
        string ToDebugString(MetadataDebugStringOptions options, int indent);

        IEnumerable<IDictionary<string, object?>> GetSeedData(bool providerValues);

        System.Linq.Expressions.LambdaExpression? GetQueryFilter();
    }
}

namespace Microsoft.EntityFrameworkCore.Internal
{
    public interface IDbContextPool<T>;
}

namespace MassTransit
{
    public sealed class ConfigurationException(string message) : Exception(message);
}
