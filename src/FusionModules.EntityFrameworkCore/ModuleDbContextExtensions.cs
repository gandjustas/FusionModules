using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// What a context composed from modules knows about its module set.
/// </summary>
public static class ModuleDbContextExtensions
{
    /// <summary>
    /// The module set the context's model is composed from, as a value to put in a model cache key;
    /// null for a context that did not call <c>UseModules</c>.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The modules' names in the order their configurations are applied, separated by <c>;</c>.</returns>
    /// <remarks>
    /// <c>UseModules</c> replaces EF Core's <c>IModelCacheKeyFactory</c> with one whose key includes the
    /// module set. A context that replaces the factory itself — a schema per tenant — replaces that one
    /// too, whichever <c>ReplaceService</c> comes last, and its key has to include this value, or the
    /// first host's model is the one every other host in the process gets:
    /// <code>
    /// public object Create(DbContext context, bool designTime) =>
    ///     (context.GetType(), ((ShopContext)context).Schema, context.GetModuleSetKey(), designTime);
    /// </code>
    /// </remarks>
    public static string? GetModuleSetKey(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetService<IDbContextOptions>().FindExtension<ModulesOptionsExtension>()?.Fingerprint;
    }
}
