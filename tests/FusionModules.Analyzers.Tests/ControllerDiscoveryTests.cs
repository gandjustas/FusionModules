using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using FusionModules.CodeFixes;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0011: a module's controllers that MVC never finds, and the fix that writes the line.
/// </summary>
public class ControllerDiscoveryTests
{
    private const string Module = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;

        [assembly: HostingStartup(typeof(Orders.Module))]

        namespace Orders;

        sealed class Module : ModuleBase
        {
        }
        """;

    // The rule.

    [Test]
    public Task AController_WithoutAnApplicationPart_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            public sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """);

    [Test]
    public Task APublicController_WithThePart_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;

            public sealed class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase { }

            static class Registration
            {
                public static void Register(IServiceCollection services) =>
                    services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
            }
            """);

    [Test]
    public Task AnInternalController_WithThePartButNotAllowed_IsReported() =>
        // MOD0001's half of the same failure: internal is what the package asks for, and MVC skips it.
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;

            public sealed class PublicController : Microsoft.AspNetCore.Mvc.ControllerBase { }

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }

            static class Registration
            {
                public static void Register(IServiceCollection services) =>
                    services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
            }
            """);

    [Test]
    public Task AnInternalController_Allowed_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            using FusionModules;
            using Microsoft.Extensions.DependencyInjection;

            internal sealed class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase { }

            static class Registration
            {
                public static void Register(IServiceCollection services) =>
                    services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly).AllowInternalControllers();
            }
            """);

    [Test]
    public Task EveryControllerMvcMisses_IsReported() =>
        // On each, so the fix is offered wherever the reader is looking.
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            internal sealed class {|MOD0011:BasketController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """);

    [Test]
    public Task WhatMvcWouldNotTakeForAController_IsNotCounted() =>
        AnalyzerTest.VerifyModuleAsync<ControllerDiscoveryAnalyzer>("""
            abstract class BaseController : Microsoft.AspNetCore.Mvc.ControllerBase { }

            [Microsoft.AspNetCore.Mvc.NonController]
            sealed class HelperController : Microsoft.AspNetCore.Mvc.ControllerBase { }

            sealed class GenericController<T> : Microsoft.AspNetCore.Mvc.ControllerBase { }

            sealed class Outer
            {
                public sealed class NestedController : Microsoft.AspNetCore.Mvc.ControllerBase { }
            }
            """);

    [Test]
    public Task ControllersOutsideAModule_AreTheHostsBusiness() =>
        AnalyzerTest.VerifyAsync<ControllerDiscoveryAnalyzer>("""
            internal sealed class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """);

    // Razor pages and views, found through the same part.

    private const string PagesModule = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;

        [assembly: HostingStartup(typeof(Status.Module))]
        // What the Razor generator writes for Pages/Status.cshtml.
        [assembly: Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItem(typeof(Status.StatusPage), "mvc.1.0.razor-page", "/Pages/Status.cshtml")]

        namespace Status;

        sealed class {|MOD0011:Module|} : ModuleBase
        {
        }

        sealed class StatusPage { }
        """;

    [Test]
    public Task RazorPagesWithoutAPart_AreReported_OnTheModule() =>
        AnalyzerTest.VerifyAsync<ControllerDiscoveryAnalyzer>(PagesModule);

    [Test]
    public Task RazorPagesWithThePart_AreFine() =>
        AnalyzerTest.VerifyAsync<ControllerDiscoveryAnalyzer>(PagesModule.Replace("{|MOD0011:Module|}", "Module") + """

            static class Registration
            {
                public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
                    Microsoft.Extensions.DependencyInjection.MvcCoreMvcBuilderExtensions.AddApplicationPart(
                        Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddRazorPages(services), typeof(Status.Module).Assembly);
            }
            """);

    [Test]
    public Task PagesBesideControllers_AreReportedOnTheControllers() =>
        // One fix covers both; the report stays where a controller is, not twice.
        AnalyzerTest.VerifyAsync<ControllerDiscoveryAnalyzer>(PagesModule.Replace("{|MOD0011:Module|}", "Module") + """

            internal sealed class {|MOD0011:StatusController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """);

    [Test]
    public Task Fix_ForRazorPages_RegistersRazorPages() =>
        VerifyFixInModuleAsync(
            PagesModule,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Status.Module))]
            // What the Razor generator writes for Pages/Status.cshtml.
            [assembly: Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItem(typeof(Status.StatusPage), "mvc.1.0.razor-page", "/Pages/Status.cshtml")]

            namespace Status;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddRazorPages().AddApplicationPart(typeof(Module).Assembly);
                }
            }

            sealed class StatusPage { }
            """);

    // The fix.

    [Test]
    public Task Fix_WritesConfigureServices_WhenTheModuleHasNone() =>
        VerifyFixAsync(
            Module,
            """
            namespace Orders;

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddControllers().AddApplicationPart(typeof(Module).Assembly).AllowInternalControllers();
                }
            }
            """);

    [Test]
    public Task Fix_AddsAStatement_ToAnExpressionBodiedOverride() =>
        VerifyFixAsync(
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.Services.AddSingleton<Module>();
            }
            """,
            """
            namespace Orders;

            public sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<Module>();
                    builder.Services.AddControllers().AddApplicationPart(typeof(Module).Assembly);
                }
            }
            """);

    [Test]
    public Task Fix_ExtendsTheModulesOwnAddControllers() =>
        // A second AddControllers() would work and read as if somebody had not looked.
        VerifyFixAsync(
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddControllers();
                }
            }
            """,
            """
            namespace Orders;

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddControllers().AddApplicationPart(typeof(Module).Assembly).AllowInternalControllers();
                }
            }
            """);

    [Test]
    public Task Fix_AllowsInternalControllers_OnTheExistingPart() =>
        VerifyFixAsync(
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.Services.AddControllers()
                        .AddApplicationPart(typeof(Module).Assembly)
                        .AddJsonOptions(_ => { });
            }
            """,
            """
            namespace Orders;

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.Services.AddControllers()
                        .AddApplicationPart(typeof(Module).Assembly).AllowInternalControllers()
                        .AddJsonOptions(_ => { });
            }
            """);

    [Test]
    public Task Fix_AllowsInternalControllers_AtTheEndOfAPartManagerChain() =>
        // A module that manages its parts by hand has no AddApplicationPart to write after.
        VerifyFixAsync(
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddControllers().ConfigureApplicationPartManager(_ => { });
                }
            }
            """,
            """
            namespace Orders;

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """,
            """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddControllers().ConfigureApplicationPartManager(_ => { }).AllowInternalControllers();
                }
            }
            """);

    [Test]
    public Task Fix_IsNotOffered_WhenThereIsNowhereToWriteIt()
    {
        // The builder is only ever an argument: no call to extend, and an action that changes nothing
        // is worse than none.
        const string module = """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders.Module))]

            namespace Orders;

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    Register(builder.Services.AddControllers().ConfigureApplicationPartManager(_ => { }));

                private static void Register(IMvcBuilder mvc) { }
            }
            """;
        const string controller = """
            namespace Orders;

            internal sealed class {|MOD0011:OrdersController|} : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """;

        var test = new CSharpCodeFixTest<ControllerDiscoveryAnalyzer, DiscoverControllersCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        var framework = new ProjectState("Framework", LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(AnalyzerTest.FrameworkStubs);
        framework.Sources.Add(AnalyzerTest.ModuleBaseStub);
        test.TestState.AdditionalProjects.Add("Framework", framework);
        test.TestState.AdditionalProjectReferences.Add("Framework");
        test.TestState.Sources.Add(("/Module.cs", module));
        test.TestState.Sources.Add(("/OrdersController.cs", controller));
        test.FixedState.Sources.Add(("/Module.cs", module));
        test.FixedState.Sources.Add(("/OrdersController.cs", controller));
        return test.RunAsync(CancellationToken.None);
    }

    private static Task VerifyFixInModuleAsync(string module, string fixedModule)
    {
        var test = new CSharpCodeFixTest<ControllerDiscoveryAnalyzer, DiscoverControllersCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        var framework = new ProjectState("Framework", LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(AnalyzerTest.FrameworkStubs);
        framework.Sources.Add(AnalyzerTest.ModuleBaseStub);
        test.TestState.AdditionalProjects.Add("Framework", framework);
        test.TestState.AdditionalProjectReferences.Add("Framework");

        test.TestState.Sources.Add(("/Module.cs", module));
        test.FixedState.Sources.Add(("/Module.cs", fixedModule));
        return test.RunAsync(CancellationToken.None);
    }

    private static Task VerifyFixAsync(string module, string controller, string fixedModule)
    {
        var test = new CSharpCodeFixTest<ControllerDiscoveryAnalyzer, DiscoverControllersCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        var framework = new ProjectState("Framework", LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(AnalyzerTest.FrameworkStubs);
        framework.Sources.Add(AnalyzerTest.ModuleBaseStub);
        test.TestState.AdditionalProjects.Add("Framework", framework);
        test.TestState.AdditionalProjectReferences.Add("Framework");

        test.TestState.Sources.Add(("/Module.cs", module));
        test.TestState.Sources.Add(("/OrdersController.cs", controller));
        test.FixedState.Sources.Add(("/Module.cs", fixedModule));
        test.FixedState.Sources.Add(("/OrdersController.cs", controller.Replace("{|MOD0011:", string.Empty).Replace("|}", string.Empty)));

        return test.RunAsync(CancellationToken.None);
    }
}
