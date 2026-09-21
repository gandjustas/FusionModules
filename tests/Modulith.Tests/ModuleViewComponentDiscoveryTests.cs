using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Tests;

/// <summary>
/// View component discovery with the public-type requirement lifted.
/// </summary>
/// <remarks>
/// Unlike controllers, this one adds a provider rather than replacing one — MVC's test for a view
/// component is a static method with no seam in it, and the feature's list is open. So the thing
/// worth asserting is that both providers contribute and neither doubles up.
/// </remarks>
public class ModuleViewComponentDiscoveryTests
{
    [Test]
    public async Task InternalViewComponent_IsNotDiscoveredWithoutTheCall()
    {
        var found = Discover(_ => { });

        await Assert.That(found).DoesNotContain(typeof(InternalTilesViewComponent));
        await Assert.That(found).Contains(typeof(PublicTilesViewComponent));
    }

    [Test]
    public async Task InternalViewComponent_IsDiscovered()
    {
        var found = Discover(builder => builder.AllowInternalViewComponents());

        await Assert.That(found).Contains(typeof(InternalTilesViewComponent));
    }

    [Test]
    public async Task PublicViewComponent_IsStillDiscoveredExactlyOnce()
    {
        // The stock provider keeps running, so the risk here is a duplicate rather than a miss.
        var found = Discover(builder => builder.AllowInternalViewComponents());

        await Assert.That(found.Count(type => type == typeof(PublicTilesViewComponent))).IsEqualTo(1);
    }

    [Test]
    public async Task CalledTwice_DiscoversEachComponentOnce()
    {
        var found = Discover(builder => builder.AllowInternalViewComponents().AllowInternalViewComponents());

        await Assert.That(found.Count(type => type == typeof(InternalTilesViewComponent))).IsEqualTo(1);
    }

    [Test]
    [Arguments(typeof(AbstractTilesViewComponent))]
    [Arguments(typeof(OuterComponents.NestedViewComponent))]
    [Arguments(typeof(OpenGenericViewComponent<>))]
    [Arguments(typeof(DeclinedViewComponent))]
    [Arguments(typeof(NotAViewComponentAtAll))]
    public async Task TypesTheStockProviderRejects_AreStillRejected(Type type)
    {
        var found = Discover(builder => builder.AllowInternalViewComponents());

        await Assert.That(found).DoesNotContain(type);
    }

    [Test]
    public async Task ComponentMarkedByAttribute_IsDiscoveredDespiteItsName()
    {
        var found = Discover(builder => builder.AllowInternalViewComponents());

        await Assert.That(found).Contains(typeof(InternalSummary));
    }

    private static IReadOnlyList<Type> Discover(Action<IMvcBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddControllersWithViews()
            .AddApplicationPart(typeof(ModuleViewComponentDiscoveryTests).Assembly);

        configure(builder);

        var manager = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ApplicationPartManager>()
            .Last();

        var feature = new ViewComponentFeature();
        manager.PopulateFeature(feature);

        return feature.ViewComponents.Select(component => component.AsType()).ToArray();
    }
}

internal class InternalTilesViewComponent : ViewComponent
{
    // Public on an internal class, because MVC finds it with public-only binding flags — and may
    // therefore take and return internal types, which is the entire point.
    public IViewComponentResult Invoke() => Content("tiles");
}

public class PublicTilesViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => Content("tiles");
}

[ViewComponent]
internal class InternalSummary : ViewComponent
{
    public IViewComponentResult Invoke() => Content("summary");
}

internal abstract class AbstractTilesViewComponent : ViewComponent;

internal class OpenGenericViewComponent<T> : ViewComponent;

[NonViewComponent]
internal class DeclinedViewComponent : ViewComponent;

internal class NotAViewComponentAtAll;

internal class OuterComponents
{
    internal class NestedViewComponent : ViewComponent;
}
