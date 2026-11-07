using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace FusionModules.Tests;

/// <summary>
/// Two modules mapping one route: a warning at startup, before the first request finds it as a 500.
/// </summary>
/// <remarks>
/// Whose an endpoint is comes from the assembly that declares its handler, and every type in this
/// test assembly is the test modules' own. So the handlers here are methods of two framework
/// assemblies — <see cref="Guid"/>'s and <see cref="Uri"/>'s — which stand for two modules.
/// </remarks>
public class RouteCollisionTests
{
    private static Delegate CoreLib() => new Func<string>(Guid.NewGuid().ToString);

    private static Delegate OtherCoreLib() => new Func<string>(string.Empty.Trim);

    private static Delegate UriAssembly() => new Func<string>(new Uri("http://orders/").ToString);

    private static Task<ModuleHost.Result> StartAsync(params ModuleBase[] modules) =>
        ModuleHost.StartAsync(modules, configureApp: app => app.UseRouting());

    private static string? Warning(ModuleHost.Result result) =>
        result.Logged.SingleOrDefault(message => message.StartsWith("Routes mapped by more than one module", StringComparison.Ordinal));

    [Test]
    public async Task TwoModulesMappingOneRoute_AreReported_WithBoth()
    {
        var result = await StartAsync(
            new MappingModule("/orders", "GET", CoreLib()),
            new MappingModule("/orders", "GET", UriAssembly()));

        await Assert.That(result.Failure).IsNull();
        await Assert.That(Warning(result)).IsEqualTo(
            "Routes mapped by more than one module match the same request, and answer it with 500 (AmbiguousMatchException): " +
            "GET /orders (System.Private.CoreLib, System.Private.Uri). " +
            "Map them to different paths, or tell them apart by HTTP method, host, content type or Order.");
    }

    [Test]
    public async Task WithTheRuntimeChecksOff_RoutesAreNotRead()
    {
        // Reading the endpoints builds them at startup instead of on the first request; with the
        // checks off they are left to the first request, and the report says why it has no counts.
        var result = await ModuleHost.StartAsync(
            [new MappingModule("/orders", "GET", CoreLib()), new MappingModule("/orders", "GET", UriAssembly())],
            configureApp: app => app.UseRouting(),
            settings: ("FusionModules:RuntimeTopologyChecks", "false"));

        await Assert.That(result.Failure).IsNull();
        await Assert.That(Warning(result)).IsNull();
        await Assert.That(result.Logged).Contains(message =>
            message.StartsWith("Topology:", StringComparison.Ordinal) &&
            message.Contains("not read: runtime topology checks are off", StringComparison.Ordinal));
    }

    [Test]
    public async Task ParameterNames_DoNotMakeTwoRoutes()
    {
        // The matcher does not see names: /orders/{id} and /orders/{orderId} take the same requests.
        var result = await StartAsync(
            new MappingModule("/orders/{id}", "GET", CoreLib()),
            new MappingModule("/Orders/{orderId}", "GET", UriAssembly()));

        await Assert.That(Warning(result)).Contains("GET /Orders/{orderId} (System.Private.CoreLib, System.Private.Uri)");
    }

    [Test]
    public async Task AnyMethod_OverlapsEveryMethod()
    {
        var result = await StartAsync(
            new MappingModule("/orders", null, CoreLib()),
            new MappingModule("/orders", "POST", UriAssembly()));

        await Assert.That(Warning(result)).Contains("POST /orders");
    }

    [Test]
    public async Task DifferentMethods_AreDifferentRoutes()
    {
        var result = await StartAsync(
            new MappingModule("/orders", "GET", CoreLib()),
            new MappingModule("/orders", "POST", UriAssembly()));

        await Assert.That(Warning(result)).IsNull();
    }

    [Test]
    public async Task DifferentConstraints_AreDifferentRoutes()
    {
        // The matcher prefers the constrained parameter, so these are not ambiguous.
        var result = await StartAsync(
            new MappingModule("/orders/{id:int}", "GET", CoreLib()),
            new MappingModule("/orders/{name}", "GET", UriAssembly()));

        await Assert.That(Warning(result)).IsNull();
    }

    [Test]
    public async Task OneOwnersTwoEndpoints_AreNotAModuleCollision()
    {
        // A bug, but not one the merge made.
        var result = await StartAsync(
            new MappingModule("/orders", "GET", CoreLib()),
            new MappingModule("/orders", "GET", OtherCoreLib()));

        await Assert.That(Warning(result)).IsNull();
    }

    [Test]
    public async Task ConventionalRoutes_ToDifferentControllers_AreDifferentRoutes()
    {
        // MapDefaultControllerRoute gives every action the template {controller}/{action}/{id?}; the
        // matcher tells them apart by their required values, controller=Home and controller=Orders.
        var result = await StartAsync(
            new ConventionalModule("Home", typeof(Guid)),
            new ConventionalModule("Orders", typeof(Uri)));

        await Assert.That(result.Failure).IsNull();
        await Assert.That(Warning(result)).IsNull();
    }

    [Test]
    public async Task ConventionalRoutes_ToOneController_Collide()
    {
        var result = await StartAsync(
            new ConventionalModule("Home", typeof(Guid)),
            new ConventionalModule("Home", typeof(Uri)));

        await Assert.That(Warning(result)).Contains("/{controller}/{action}/{id?} (System.Private.CoreLib, System.Private.Uri)");
    }

    [Test]
    public async Task ARazorPage_BelongsToTheAssemblyOfItsPage()
    {
        // A page's endpoint names no handler method; the page type, compiled into the module, does.
        var result = await StartAsync(
            new PageModule("/status", typeof(Guid)),
            new PageModule("/status", typeof(Uri)));

        await Assert.That(Warning(result)).Contains("/status (System.Private.CoreLib, System.Private.Uri)");
    }

    [Test]
    public async Task AnEndpointThatCannotBeBuilt_DoesNotStopTheHost()
    {
        // Checking the routes builds the endpoints, which ASP.NET Core otherwise leaves to the first
        // request. A handler that needs what the topology lacks fails there; it must not fail startup,
        // which it never did without this check.
        var result = await StartAsync(new MappingModule("/orders", "GET", new Func<IUnregistered, string>(service => service.Name)));

        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.Logged).Contains(message => message.StartsWith("Endpoints could not be read at startup", StringComparison.Ordinal));
        await Assert.That(result.Logged).Contains(message => message.StartsWith("Topology:", StringComparison.Ordinal) && message.Contains("endpoints: not read"));
    }

    internal interface IUnregistered
    {
        string Name { get; }
    }

    /// <summary>Maps an endpoint shaped as a Razor Page's: no handler method, a compiled page type.</summary>
    private sealed class PageModule(string pattern, Type page) : ModuleBase
    {
        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints => endpoints
                .Map(pattern, context => Task.CompletedTask)
                .WithMetadata(new CompiledPageActionDescriptor { PageTypeInfo = page.GetTypeInfo() }));
    }

    /// <summary>Maps an endpoint shaped as a conventionally routed action's, owned by <paramref name="controller"/>'s assembly.</summary>
    private sealed class ConventionalModule(string controllerName, Type controller) : ModuleBase
    {
        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints =>
            {
                var pattern = RoutePatternFactory.Parse(
                    "{controller}/{action}/{id?}",
                    defaults: null,
                    parameterPolicies: null,
                    requiredValues: new { controller = controllerName, action = "Index" });
                var endpoint = new RouteEndpoint(
                    context => Task.CompletedTask,
                    pattern,
                    order: 1,
                    new EndpointMetadataCollection(new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo() }),
                    $"{controllerName}.Index");
                endpoints.DataSources.Add(new DefaultEndpointDataSource(endpoint));
            });
    }

    private sealed class MappingModule(string pattern, string? method, Delegate handler) : ModuleBase
    {
        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints =>
            {
                if (method is null)
                {
                    endpoints.Map(pattern, handler);
                }
                else
                {
                    endpoints.MapMethods(pattern, [method], handler);
                }
            });
    }
}
