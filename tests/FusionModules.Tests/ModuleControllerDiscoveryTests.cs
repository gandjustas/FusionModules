using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tests;

/// <summary>
/// Controller discovery with the public-type requirement lifted.
/// </summary>
/// <remarks>
/// Through the application part manager rather than over HTTP: what the call changes is which
/// types MVC considers controllers, and that is a question the feature answers directly. The
/// sample's integration test asserts the other half — that a discovered internal controller
/// actually serves a request.
/// </remarks>
public class ModuleControllerDiscoveryTests
{
    [Test]
    public async Task InternalController_IsDiscovered()
    {
        var controllers = Discover(builder => builder.AllowInternalControllers());

        await Assert.That(controllers).Contains(typeof(InternalOrdersController));
    }

    [Test]
    public async Task InternalController_IsNotDiscoveredWithoutTheCall()
    {
        // The premise. Without it the rest of this file is asserting nothing.
        var controllers = Discover(_ => { });

        await Assert.That(controllers).DoesNotContain(typeof(InternalOrdersController));
        await Assert.That(controllers).Contains(typeof(PublicOrdersController));
    }

    [Test]
    public async Task PublicController_IsStillDiscovered()
    {
        var controllers = Discover(builder => builder.AllowInternalControllers());

        await Assert.That(controllers).Contains(typeof(PublicOrdersController));
    }

    [Test]
    [Arguments(typeof(AbstractController))]
    [Arguments(typeof(Outer.NestedController))]
    [Arguments(typeof(OpenGenericController<>))]
    [Arguments(typeof(DeclinedController))]
    [Arguments(typeof(NotAControllerAtAll))]
    public async Task TypesTheStockProviderRejects_AreStillRejected(Type type)
    {
        // Only the IsPublic check is lifted. Everything else MVC decides, it still decides.
        var controllers = Discover(builder => builder.AllowInternalControllers());

        await Assert.That(controllers).DoesNotContain(type);
    }

    [Test]
    public async Task CalledTwice_DiscoversEachControllerOnce()
    {
        var controllers = Discover(builder => builder.AllowInternalControllers().AllowInternalControllers());

        await Assert.That(controllers.Count(type => type == typeof(InternalOrdersController))).IsEqualTo(1);
    }

    [Test]
    public async Task AnotherModuleCallingAddControllers_DoesNotRestoreTheStockProvider()
    {
        // The multi-module shape: one module makes the call, another simply registers MVC after
        // it. MVC adds its own provider only when no ControllerFeatureProvider is present, and
        // ours is one — which is why it is a subclass rather than a fresh implementation.
        var services = new ServiceCollection();
        services.AddControllers().AddApplicationPart(typeof(ModuleControllerDiscoveryTests).Assembly).AllowInternalControllers();
        services.AddControllersWithViews();

        var controllers = Populate(services);

        await Assert.That(controllers).Contains(typeof(InternalOrdersController));
        await Assert.That(controllers.Count(type => type == typeof(PublicOrdersController))).IsEqualTo(1);
    }

    [Test]
    public async Task TheProviderTakesThePlaceOfTheOneItReplaced()
    {
        // Providers run in list order, so a provider that filters what discovery found has to run
        // after it. Appending instead of replacing in place would silently invert that.
        var services = new ServiceCollection();
        var builder = services.AddControllers().AddApplicationPart(typeof(ModuleControllerDiscoveryTests).Assembly);

        builder.ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new DropsEverythingItFinds()));

        builder.AllowInternalControllers();

        await Assert.That(Populate(services)).IsEmpty();
    }

    private static IReadOnlyList<Type> Discover(Action<IMvcBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddControllers().AddApplicationPart(typeof(ModuleControllerDiscoveryTests).Assembly);

        configure(builder);

        return Populate(services);
    }

    private static IReadOnlyList<Type> Populate(IServiceCollection services)
    {
        var manager = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ApplicationPartManager>()
            .Last();

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);

        return feature.Controllers.Select(controller => controller.AsType()).ToArray();
    }

    /// <summary>Runs after discovery and empties the result — proving it ran after discovery.</summary>
    private sealed class DropsEverythingItFinds : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Clear();
    }
}

internal class InternalOrdersController : ControllerBase;

public class PublicOrdersController : ControllerBase;

internal abstract class AbstractController : ControllerBase;

internal class OpenGenericController<T> : ControllerBase;

[NonController]
internal class DeclinedController : ControllerBase;

internal class NotAControllerAtAll;

internal class Outer
{
    internal class NestedController : ControllerBase;
}
