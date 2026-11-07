using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// A model cache key that includes the module set, so that each set gets a model of its own.
/// </summary>
/// <remarks>
/// EF Core builds a context's model once and caches it under the context's type. Hosts with
/// different modules in one process — every integration-test assembly — would otherwise share the
/// first one's model: no exception, only the wrong tables.
/// </remarks>
internal sealed class ModuleModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.GetModuleSetKey(), designTime);
}
