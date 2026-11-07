using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tests;

/// <summary>
/// Asks MVC's application part manager what it would discover, without starting a host.
/// </summary>
internal static class Features
{
    /// <summary>
    /// Populates a feature from the manager the last <c>AddMvc</c>-family call registered,
    /// exactly as MVC does when the application starts.
    /// </summary>
    public static TFeature Populate<TFeature>(IServiceCollection services)
        where TFeature : new()
    {
        var manager = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ApplicationPartManager>()
            .Last();

        var feature = new TFeature();
        manager.PopulateFeature(feature);

        return feature;
    }
}
