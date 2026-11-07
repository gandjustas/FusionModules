using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using FusionModules.CodeFixes;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// The fix for MOD0002: a hand-written <c>IHostingStartup</c> becomes a <c>ModuleBase</c> module,
/// and what <c>ModuleBase</c> does for itself — registering the module as a startup filter — goes.
/// </summary>
public class UseModuleBaseTests
{
    [Test]
    public Task ConfigureServices_MovesIntoTheBuilderOverload() =>
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Service { }

            sealed class Orders : IHostingStartup
            {
                public void Configure(IWebHostBuilder builder)
                {
                    builder.ConfigureServices(services =>
                    {
                        services.AddSingleton<Service>();
                    });
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders))]

            sealed class Service { }

            sealed class Orders : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<Service>();
                }
            }
            """);

    [Test]
    public Task TheStartupFilter_BecomesTheConfigureOverride_AndItsRegistrationGoes() =>
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Service { }

            static class Pipeline
            {
                public static void UseOrders(this IApplicationBuilder app) { }
            }

            sealed class Orders : IHostingStartup, IStartupFilter
            {
                public void Configure(IWebHostBuilder builder)
                {
                    builder.ConfigureServices((context, services) =>
                    {
                        services.AddSingleton<IStartupFilter>(this);
                        if (context.HostingEnvironment.EnvironmentName == "Development")
                        {
                            services.AddSingleton<Service>();
                        }
                    });
                }

                public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next) => app =>
                {
                    next(app);
                    app.UseOrders();
                };
            }
            """,
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders))]

            sealed class Service { }

            static class Pipeline
            {
                public static void UseOrders(this IApplicationBuilder app) { }
            }

            sealed class Orders : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    if (builder.Environment.EnvironmentName == "Development")
                    {
                        builder.Services.AddSingleton<Service>();
                    }
                }

                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseOrders();
                }
            }
            """);

    [Test]
    public Task OtherCallsOnTheWebHostBuilder_HaveNoCounterpart_AndAreLeftAlone() =>
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Orders : IHostingStartup
            {
                public void Configure(IWebHostBuilder builder) => builder.UseSetting("key", "value");
            }
            """,
            fixedSource: null);

    [Test]
    public Task AFilterThatRunsBeforeNext_HasNoCounterpart_AndIsLeftAlone() =>
        // ModuleBase calls Configure after next; middleware written to run first cannot move there.
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            static class Pipeline
            {
                public static void UseOrders(this IApplicationBuilder app) { }
            }

            sealed class Orders : IHostingStartup, IStartupFilter
            {
                public void Configure(IWebHostBuilder builder) { }

                public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next) => app =>
                {
                    app.UseOrders();
                    next(app);
                };
            }
            """,
            fixedSource: null);

    [Test]
    public Task ARegistrationOfTheModuleAsAStartupFilter_IsRemoved() =>
        VerifyAsync<ModuleUsageAnalyzer, RemoveStartupFilterRegistrationCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Service { }

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    {|MOD0009:builder.Services.AddSingleton<IStartupFilter>(this)|};
                    builder.Services.AddSingleton<Service>();
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Service { }

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<Service>();
                }
            }
            """);

    [Test]
    public Task ARegistrationInAChain_LeavesTheRestOfTheChain() =>
        VerifyAsync<ModuleUsageAnalyzer, RemoveStartupFilterRegistrationCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Service { }

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    {|MOD0009:builder.Services.AddSingleton<IStartupFilter>(this)|}.AddSingleton<Service>();
            }
            """,
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Service { }

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.Services.AddSingleton<Service>();
            }
            """);

    [Test]
    public Task AConfigureHelperOfTheSameName_IsLeftAlone() =>
        // The filter calls a private Configure(IApplicationBuilder); an override of that name
        // would collide with it, or call itself.
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Orders : IHostingStartup, IStartupFilter
            {
                public void Configure(IWebHostBuilder builder) { }

                public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next) => app =>
                {
                    next(app);
                    Configure(app);
                };

                private static void Configure(IApplicationBuilder app) { }
            }
            """,
            fixedSource: null);

    [Test]
    public Task TwoLambdasWithTheSameLocal_EachKeepItsOwnBlock() =>
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Orders : IHostingStartup
            {
                public void Configure(IWebHostBuilder builder)
                {
                    builder.ConfigureServices(services =>
                    {
                        var name = "orders";
                    });
                    builder.ConfigureServices(services =>
                    {
                        var name = "billing";
                    });
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(Orders))]

            sealed class Orders : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    {
                        var name = "orders";
                    }
                    {
                        var name = "billing";
                    }
                }
            }
            """);

    [Test]
    public Task AReturnInAnyButTheLastLambda_IsLeftAlone() =>
        VerifyAsync(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            [assembly: HostingStartup(typeof({|MOD0002:Orders|}))]

            sealed class Service { }

            sealed class Orders : IHostingStartup
            {
                public void Configure(IWebHostBuilder builder)
                {
                    builder.ConfigureServices(services =>
                    {
                        return;
                    });
                    builder.ConfigureServices(services => services.AddSingleton<Service>());
                }
            }
            """,
            fixedSource: null);

    [Test]
    public Task ARegistrationAsTheBodyOfAnIf_LeavesAnEmptyBlock() =>
        VerifyAsync<ModuleUsageAnalyzer, RemoveStartupFilterRegistrationCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    if (builder.Configuration["Eager"] is not null)
                        {|MOD0009:builder.Services.AddSingleton<IStartupFilter>(this)|};
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            [assembly: HostingStartup(typeof(Eager))]

            sealed class Eager : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    if (builder.Configuration["Eager"] is not null)
                        { }
                }
            }
            """);

    private static Task VerifyAsync(string source, string? fixedSource) =>
        VerifyAsync<ModuleAnalyzer, UseModuleBaseCodeFixProvider>(source, fixedSource);

    private static async Task VerifyAsync<TAnalyzer, TCodeFix>(string source, string? fixedSource)
        where TAnalyzer : Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer, new()
        where TCodeFix : Microsoft.CodeAnalysis.CodeFixes.CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };
        await AnalyzerTest.ArrangeAsync(test.TestState, withModuleBase: true, [source]);
        await AnalyzerTest.ArrangeAsync(test.FixedState, withModuleBase: true, [fixedSource ?? source]);

        await test.RunAsync(CancellationToken.None);
    }
}
