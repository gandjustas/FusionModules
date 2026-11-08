using System.Collections.Immutable;
using FusionModules.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0015: a program whose own registrations and endpoints can move into a module of the same
/// project, the fix that moves them, and the same conversion as a refactoring.
/// </summary>
public class ProgramToModuleTests
{
    /// <summary>What a program meets beyond <see cref="AnalyzerTest.FrameworkStubs"/>: routing, middleware, more registrations.</summary>
    private const string WebStubs = """
        namespace Microsoft.AspNetCore.Routing
        {
            public sealed class RouteGroupBuilder : IEndpointRouteBuilder { }

            public sealed class RouteHandlerBuilder { }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public static class EndpointStubExtensions
            {
                public static Microsoft.AspNetCore.Routing.RouteHandlerBuilder MapGet(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, string pattern, System.Delegate handler) => null;
                public static Microsoft.AspNetCore.Routing.RouteGroupBuilder MapGroup(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, string prefix) => null;
                public static Microsoft.AspNetCore.Routing.RouteHandlerBuilder MapFallback(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, System.Delegate handler) => null;
                public static Microsoft.AspNetCore.Routing.RouteHandlerBuilder MapRazorComponents<TRoot>(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) => null;
                public static void MapControllers(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
                public static void MapHub<THub>(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, string pattern) where THub : Microsoft.AspNetCore.SignalR.Hub { }
                public static WebApplication MapDefaultEndpoints(this WebApplication app) => app;
            }

            public static class ApplicationStubExtensions
            {
                public static IApplicationBuilder UseRouting(this IApplicationBuilder app) => app;
                public static IApplicationBuilder UseAuthorization(this IApplicationBuilder app) => app;
                public static IApplicationBuilder UseAuthentication(this IApplicationBuilder app) => app;
                public static IApplicationBuilder UseAntiforgery(this IApplicationBuilder app) => app;
                public static IApplicationBuilder UseEndpoints(this IApplicationBuilder app, System.Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> configure) => app;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class ServiceStubExtensions
            {
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
                public static IServiceCollection AddAuthentication(this IServiceCollection services) => services;
                public static IServiceCollection AddDbContext<TContext>(this IServiceCollection services, System.Action<object> configure = null) => services;
                public static IServiceCollection Configure<TOptions>(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration section) where TOptions : class => services;
                public static IServiceCollection Replace(this IServiceCollection services, object descriptor) => services;
                public static IServiceCollection Decorate<TService, TDecorator>(this IServiceCollection services) where TDecorator : TService => services;
                public static IServiceCollection AddValidatorsFromAssembly(this IServiceCollection services, System.Reflection.Assembly assembly) => services;
                public static IServiceCollection AddMassTransit(this IServiceCollection services, System.Action<object> configure) => services;
                public static Microsoft.Extensions.Options.OptionsBuilder<TOptions> AddOptions<TOptions>(this IServiceCollection services) where TOptions : class => null;
            }
        }

        namespace Microsoft.Extensions.Options
        {
            public sealed class OptionsBuilder<TOptions> where TOptions : class
            {
                public OptionsBuilder<TOptions> PostConfigure(System.Action<TOptions> configure) => this;
            }
        }

        namespace Microsoft.Extensions.Hosting
        {
            public static class EnvironmentStubExtensions
            {
                public static bool IsDevelopment(this IHostEnvironment environment) => true;
            }
        }

        namespace Asp.Versioning.Builder
        {
            /// <summary>A C# 14 extension block, as Asp.Versioning 10 declares NewVersionedApi.</summary>
            public static class VersioningStubExtensions
            {
                extension(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
                {
                    public Microsoft.AspNetCore.Routing.RouteGroupBuilder NewVersionedApi(string name) => null;
                }
            }
        }

        namespace Identity.Contracts
        {
            /// <summary>Someone else's contract, which the host's registrations register too.</summary>
            public interface IProfileService { }

            /// <summary>Its owner's implementation.</summary>
            public sealed class DefaultProfileService : IProfileService
            {
                public static readonly IProfileService Instance = new DefaultProfileService();
            }
        }

        namespace Microsoft.Extensions.Configuration
        {
            public static class ConfigurationStubExtensions
            {
                public static string GetConnectionString(this IConfiguration configuration, string name) => null;
                public static IConfiguration GetSection(this IConfiguration configuration, string key) => configuration;
                public static IConfigurationManager AddJsonFile(this IConfigurationManager configuration, string path) => configuration;
            }
        }
        """;

    private static readonly Lazy<Task<MetadataReference>> Web = new(async () =>
        AnalyzerTest.Compile("WebStubs", [WebStubs], [.. await AnalyzerTest.BaseReferences.Value, await AnalyzerTest.Framework.Value]));

    /// <summary>The service's own code, beside its Program.cs.</summary>
    private const string OwnCode = """
        using Microsoft.Extensions.Hosting;

        namespace Ordering.API
        {
            internal static class Extensions
            {
                public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder) => builder;
            }

            internal interface IOrderRepository { }

            internal sealed class OrderRepository : IOrderRepository { }
        }
        """;

    [Test]
    public async Task OnlyTheProjectsOwnRegistrationsAndEndpointsMove()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Ordering.API;
            using Ordering.Infrastructure;

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            builder.AddEventBus("eventbus").AddSubscription<OrderRepository>();
            builder.Services.AddControllers();
            builder.Services.AddAuthentication();
            // The service's own registrations.
            {|MOD0015:builder.AddApplicationServices();|}
            builder.Services.AddScoped<IOrderRepository, OrderRepository>();
            builder.Services.AddDbContext<OrderingContext>();
            var app = builder.Build();
            app.UseAuthorization();
            app.MapGet("/orders", () => "orders");
            app.MapDefaultEndpoints();
            app.Run();
            """);
        test.TestState.Sources.Add(OwnCode);
        await AddSolutionAsync(test);

        test.FixedState.Sources.Add("""
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Ordering.API;

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            builder.AddEventBus("eventbus").AddSubscription<OrderRepository>();
            builder.Services.AddControllers();
            builder.Services.AddAuthentication();
            var app = builder.Build();
            app.UseModuleRouting(authorization: false);
            app.UseAuthorization();
            app.MapDefaultEndpoints();
            app.Run();
            """);
        test.FixedState.Sources.Add(OwnCode);
        // AddDbContext<OrderingContext> moves too: Ordering.Infrastructure is referenced by no
        // other program of the solution, unlike ServiceDefaults.
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Ordering.API;
            using Ordering.Infrastructure;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    // The service's own registrations.
                    builder.AddApplicationServices();
                    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
                    builder.Services.AddDbContext<OrderingContext>();
                }

                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AStartupClass_GivesUpItsOwnRegistrationsAndTheEndpointsAtTheEndOfConfigure()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            public static class Program
            {
                public static void Main(string[] args) =>
                    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(web => web.UseStartup<Startup>());
            }

            public sealed class Startup
            {
                public Startup(IConfiguration configuration) => Configuration = configuration;

                public IConfiguration Configuration { get; }

                public void ConfigureServices(IServiceCollection services)
                {
                    services.AddControllers();
                    {|MOD0015:var section = Configuration.GetSection("Orders");|}
                    services.Configure<OrdersOptions>(section);
                    services.AddScoped<IOrders, Orders>();
                }

                public void Configure(IApplicationBuilder app)
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            internal sealed class OrdersOptions { }

            internal interface IOrders { }

            internal sealed class Orders : IOrders { }
            """);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            public static class Program
            {
                public static void Main(string[] args) =>
                    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(web => web.UseStartup<Startup>());
            }

            public sealed class Startup
            {
                public Startup(IConfiguration configuration) => Configuration = configuration;

                public IConfiguration Configuration { get; }

                public void ConfigureServices(IServiceCollection services)
                {
                    services.AddControllers();
                }

                public void Configure(IApplicationBuilder app)
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                    });
                }
            }

            internal sealed class OrdersOptions { }

            internal interface IOrders { }

            internal sealed class Orders : IOrders { }
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var section = builder.Configuration.GetSection("Orders");
                    builder.Services.Configure<OrdersOptions>(section);
                    builder.Services.AddScoped<IOrders, Orders>();
                }

                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AMainMethod_KeepsWhatUsesALocalThatStays_AndALocalOnlyMovedCodeUsesMovesToo()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            internal static class Program
            {
                private static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    {|MOD0015:var connectionString = builder.Configuration.GetConnectionString("orders");|}
                    var name = "orders";
                    builder.Services.AddDbContext<OrdersContext>(_ => System.Console.WriteLine(connectionString));
                    builder.Services.AddSingleton(new OrdersMarker(name));
                    System.Console.WriteLine(name);
                    var app = builder.Build();
                    app.Run();
                }
            }

            internal sealed class OrdersContext { }

            internal sealed class OrdersMarker(string name)
            {
                public string Name { get; } = name;
            }
            """);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            internal static class Program
            {
                private static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    var name = "orders";
                    builder.Services.AddSingleton(new OrdersMarker(name));
                    System.Console.WriteLine(name);
                    var app = builder.Build();
                    app.Run();
                }
            }

            internal sealed class OrdersContext { }

            internal sealed class OrdersMarker(string name)
            {
                public string Name { get; } = name;
            }
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var connectionString = builder.Configuration.GetConnectionString("orders");
                    builder.Services.AddDbContext<OrdersContext>(_ => System.Console.WriteLine(connectionString));
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task FixAll_ConvertsEveryDocument_AndTheSecondAddsToTheModuleTheFirstCreated()
    {
        const string Program = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            var startup = new Startup(builder.Configuration);
            startup.ConfigureServices(builder.Services);
            {|MOD0015:builder.Services.AddSingleton<OrdersMarker>();|}
            var app = builder.Build();
            startup.Configure(app);
            app.Run();

            internal sealed class OrdersMarker { }
            """;
        const string Startup = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            internal sealed class Startup(IConfiguration configuration)
            {
                public IConfiguration Configuration { get; } = configuration;

                public void ConfigureServices(IServiceCollection services)
                {
                    {|MOD0015:services.AddScoped<IOrders, Orders>();|}
                }

                public void Configure(IApplicationBuilder app)
                {
                }
            }

            internal interface IOrders { }

            internal sealed class Orders : IOrders { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add(Program);
        test.TestState.Sources.Add(Startup);

        var programFixed = """
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var startup = new Startup(builder.Configuration);
            startup.ConfigureServices(builder.Services);
            var app = builder.Build();
            startup.Configure(app);
            app.Run();

            internal sealed class OrdersMarker { }
            """;
        var startupFixed = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            internal sealed class Startup(IConfiguration configuration)
            {
                public IConfiguration Configuration { get; } = configuration;

                public void ConfigureServices(IServiceCollection services)
                {
                }

                public void Configure(IApplicationBuilder app)
                {
                }
            }

            internal interface IOrders { }

            internal sealed class Orders : IOrders { }
            """;

        // One fix converts the document it is on; with a module in the project, the other document
        // is no longer reported.
        test.FixedState.Sources.Add(programFixed);
        test.FixedState.Sources.Add(Startup.Replace("{|MOD0015:", string.Empty).Replace(";|}", ";"));
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<OrdersMarker>();
                }
            }

            """));

        // Fix all — what dotnet format runs — converts both, one after the other.
        test.BatchFixedState.Sources.Add(programFixed);
        test.BatchFixedState.Sources.Add(startupFixed);
        test.BatchFixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<OrdersMarker>();
                    builder.Services.AddScoped<IOrders, Orders>();
                }
            }

            """));
        test.BatchFixedState.OutputKind = OutputKind.ConsoleApplication;
        test.CodeFixTestBehaviors = CodeFixTestBehaviors.SkipFixAllInDocumentCheck;

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task TheBlankLinesBetweenTheProgramsGroups_Stay()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();

            {|MOD0015:builder.Services.AddSingleton<OrdersMarker>();|}
            builder.Services.AddControllers();

            var app = builder.Build();
            app.UseRouting();

            app.MapGet("/orders", () => "orders");
            app.MapControllers();

            app.Run();

            internal sealed class OrdersMarker { }
            """);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();

            builder.Services.AddControllers();

            var app = builder.Build();
            app.UseRouting();

            app.MapControllers();

            app.Run();

            internal sealed class OrdersMarker { }
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<OrdersMarker>();
                }

                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public Task NothingOfTheProjectsOwn_IsNotReported() =>
        VerifySilentAsync(projectKind: null, """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddAuthentication();
            var app = builder.Build();
            app.UseRouting();
            app.MapControllers();
            app.Run();
            """);

    [Test]
    public Task OrderSensitiveCallsAndReadsAfterNewConfigurationSources_Stay() =>
        VerifySilentAsync(projectKind: null, """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.AddJsonFile("orders.json");
            // Read now, in the module it would be read before orders.json is added.
            var connectionString = builder.Configuration.GetConnectionString("orders");
            builder.Services.AddSingleton(new OrdersMarker(connectionString));
            // The module runs first: there would be nothing yet to replace.
            builder.Services.Replace(new OrdersMarker("replacement"));
            var app = builder.Build();
            app.Run();

            internal sealed class OrdersMarker(string name)
            {
                public string Name { get; } = name;
            }
            """);

    [Test]
    [Arguments("app.MapControllers();", false)]
    [Arguments("app.MapDefaultEndpoints();", true)]
    public async Task ARoutingCall_IsLeftOutOnlyWhereTheProgramSurelyKeepsEndpoints(string kept, bool routing)
    {
        // A framework mapping written in the program maps whenever it runs. MapDefaultEndpoints may
        // map nothing — eShop's maps /health in Development only — and then, without routing, the
        // module's UseEndpoints would throw at startup.
        var test = await FixTestAsync();
        test.TestState.Sources.Add($$"""
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {|MOD0015:app.MapGet("/orders", () => "orders");|}
            {{kept}}
            app.Run();
            """);

        test.FixedState.Sources.Add($$"""
            {{(routing ? "using FusionModules;\n" : string.Empty)}}using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {{(routing ? "app.UseModuleRouting();\n" : string.Empty)}}{{kept}}
            app.Run();
            """);

        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public Task TheMessageBus_Stays_ThoughItsConsumersAreTheProjects() =>
        VerifySilentAsync(projectKind: null, """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            // One bus per process, with its transport: two modules each adding one would collide.
            builder.Services.AddMassTransit(bus => System.Console.WriteLine(typeof(OrderPlacedConsumer)));
            var app = builder.Build();
            app.Run();

            internal sealed class OrderPlacedConsumer { }
            """);

    [Test]
    public Task TheApplicationsShellAndFallback_Stay_WhateverTheyName() =>
        VerifySilentAsync(projectKind: null, """
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            // The project's own root component, and the whole application's /_blazor with it.
            app.MapRazorComponents<App>();
            // A catch-all: in a monolith it would answer for every module.
            app.MapFallback(() => "index.html");
            app.Run();

            internal sealed class App { }
            """);

    [Test]
    public Task AHostWithAModuleOfItsOwn_IsNotReported() =>
        VerifySilentAsync(projectKind: null, """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<OrdersMarker>();
            var app = builder.Build();
            app.Run();

            internal sealed class OrdersMarker { }
            """, AnalyzerTest.RegisteredModule);

    [Test]
    public Task ATestProject_IsNotReported() =>
        VerifySilentAsync(projectKind: "Test", """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<OrdersMarker>();
            var app = builder.Build();
            app.Run();

            internal sealed class OrdersMarker { }
            """);

    [Test]
    public async Task TheRefactoring_IsOfferedOnProgramCs_AndCreatesTheModule()
    {
        const string Source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<OrdersMarker>();
            var app = builder.Build();
            app.MapGet("/orders", () => "orders");
            app.Run();

            internal sealed class OrdersMarker { }
            """;

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "Orders.Api",
            "Orders.Api",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication),
            metadataReferences: [.. await AnalyzerTest.BaseReferences.Value, await AnalyzerTest.Framework.Value, await Web.Value]));
        var document = workspace.AddDocument(project.Id, "Program.cs", SourceText.From(Source));

        var actions = new List<CodeAction>();
        var context = new CodeRefactoringContext(
            document, new TextSpan(Source.IndexOf("AddSingleton", StringComparison.Ordinal), 0), actions.Add, CancellationToken.None);
        await new ProgramToModuleRefactoringProvider().ComputeRefactoringsAsync(context);

        await Assert.That(actions).HasSingleItem();
        await Assert.That(actions[0].Title).IsEqualTo("Move this project's own registrations into a module");

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var module = changed.GetProject(project.Id)!.Documents.Single(candidate => candidate.Name == "Module.cs");
        var text = (await module.GetTextAsync()).ToString();

        await Assert.That(text).Contains("[assembly: HostingStartup(typeof(Orders.Api.Module))]");
        await Assert.That(text).Contains("builder.Services.AddSingleton<OrdersMarker>();");
        await Assert.That(text).Contains("endpoints.MapGet(\"/orders\", () => \"orders\");");

        var compilation = await changed.GetProject(project.Id)!.GetCompilationAsync();
        await Assert.That(compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task ABuilderFromAFactory_AndLocalsNamingItsParts_AreTheModulesBuilder()
    {
        const string Types = """

            internal static class ServiceHost
            {
                public static WebApplicationBuilder CreateBuilder(string[] args) => WebApplication.CreateBuilder(args);
            }

            internal interface IOrderRepository { }

            internal sealed class OrderRepository : IOrderRepository { }

            internal sealed class OrdersOptions { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            var builder = ServiceHost.CreateBuilder(args);
            var configuration = builder.Configuration;
            var services = builder.Services;
            {|MOD0015:services.AddScoped<IOrderRepository, OrderRepository>();|}
            builder.Services.Configure<OrdersOptions>(configuration.GetSection("Orders"));
            var app = builder.Build();
            app.Run();
            """ + Types);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;

            var builder = ServiceHost.CreateBuilder(args);
            var app = builder.Build();
            app.Run();
            """ + Types);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
                    builder.Services.Configure<OrdersOptions>(builder.Configuration.GetSection("Orders"));
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task SomeoneElsesContract_AfterARegistrationThatStays_Stays_AndAChainLosesOnlyItsOwnCalls()
    {
        const string Types = """

            namespace Api
            {
                internal static class ApiRegistrations
                {
                    // The whole application's business as well as the service's: it stays.
                    public static IServiceCollection AddApi(this IServiceCollection services) =>
                        services.AddAuthentication().AddScoped<IOrderRepository, OrderRepository>();
                }
            }

            internal static class Registrations
            {
                public static IServiceCollection AddPersistence(this IServiceCollection services) =>
                    services.AddDbContext<OrdersContext>();
            }

            internal interface IOrderRepository { }

            internal sealed class OrderRepository : IOrderRepository { }

            internal sealed class OrdersContext { }

            internal sealed class ProfileService : IProfileService { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Api;
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();
            // Last wins: in the module it would come before whatever AddAuthentication registers.
            builder.Services.AddScoped<IProfileService, ProfileService>();
            {|MOD0015:builder.Services.AddScoped<IOrderRepository, OrderRepository>();|}
            builder.Services
                .AddApi()
                .AddPersistence();
            var app = builder.Build();
            app.Run();
            """ + Types);

        test.FixedState.Sources.Add("""
            using Api;
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();
            // Last wins: in the module it would come before whatever AddAuthentication registers.
            builder.Services.AddScoped<IProfileService, ProfileService>();
            builder.Services
                .AddApi();
            var app = builder.Build();
            app.Run();
            """ + Types);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
                    builder.Services.AddPersistence();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AChainStartingWithAnOwnCall_GivesUpOnlyItsOwnCalls()
    {
        const string Types = """

            internal interface IOrderRepository { }

            internal sealed class OrderRepository : IOrderRepository { }

            internal interface IOrderDetails { }

            internal sealed class OrderDetails : IOrderDetails { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            {|MOD0015:builder.Services.AddScoped<IOrderRepository, OrderRepository>()|}.AddAuthentication();
            builder.Services.AddScoped<IOrderDetails, OrderDetails>().AddSingleton<IProfileService>(DefaultProfileService.Instance);
            var app = builder.Build();
            app.Run();
            """ + Types);

        test.FixedState.Sources.Add("""
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();
            builder.Services.AddSingleton<IProfileService>(DefaultProfileService.Instance);
            var app = builder.Build();
            app.Run();
            """ + Types);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
                    builder.Services.AddScoped<IOrderDetails, OrderDetails>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AnOwnMethodRegisteringSomeoneElsesContract_MovesOnlyWhenNothingBeforeItStays()
    {
        const string Types = """

            internal static class Profiles
            {
                public static IServiceCollection AddProfiles(this IServiceCollection services) =>
                    services.AddSingleton<IProfileService, ProfileService>();

                public static IServiceCollection AddAuditProfiles(this IServiceCollection services) =>
                    services.AddSingleton<IProfileService, AuditProfileService>();
            }

            internal sealed class ProfileService : IProfileService { }

            internal sealed class AuditProfileService : IProfileService { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            {|MOD0015:builder.Services.AddProfiles();|}
            builder.Services.AddSingleton<IProfileService>(DefaultProfileService.Instance);
            // Last wins: in the module it would come before the registration above.
            builder.Services.AddAuditProfiles();
            var app = builder.Build();
            app.Run();
            """ + Types);

        test.FixedState.Sources.Add("""
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<IProfileService>(DefaultProfileService.Instance);
            // Last wins: in the module it would come before the registration above.
            builder.Services.AddAuditProfiles();
            var app = builder.Build();
            app.Run();
            """ + Types);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddProfiles();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AStartup_MovesAnIfOnItsEnvironment_AndKeepsSomeoneElsesContractAfterWhatStays()
    {
        const string Startup = """
            using Identity.Contracts;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            public static class Program
            {
                public static void Main(string[] args) =>
                    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(web => web.UseStartup<Startup>());
            }

            public sealed class Startup
            {
                public Startup(IWebHostEnvironment environment) => Environment = environment;

                public IWebHostEnvironment Environment { get; }

                public void ConfigureServices(IServiceCollection services)
                {
                    services.AddAuthentication();
            {0}        services.AddScoped<IProfileService, ProfileService>();
                }

                public void Configure(IApplicationBuilder app)
                {
                }
            }

            internal sealed class OrdersSeed { }

            internal sealed class ProfileService : IProfileService { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add(Startup.Replace("{0}", """
                    {|MOD0015:if (Environment.IsDevelopment())
                    {
                        services.AddSingleton<OrdersSeed>();
                    }|}

            """));
        test.FixedState.Sources.Add(Startup.Replace("{0}", string.Empty));
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    if (builder.Environment.IsDevelopment())
                    {
                        builder.Services.AddSingleton<OrdersSeed>();
                    }
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ADecoratorPostConfigureAssemblyScanAndEnvironmentCheck_MoveWithWhatTheyActOn()
    {
        const string Types = """

            internal interface IOrderRepository { }

            internal sealed class OrderRepository : IOrderRepository { }

            internal sealed class CachedOrderRepository : IOrderRepository { }

            internal sealed class OrdersOptions { }

            internal sealed class OrdersSeed { }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();
            {|MOD0015:builder.Services.AddScoped<IOrderRepository, OrderRepository>();|}
            builder.Services.Decorate<IOrderRepository, CachedOrderRepository>();
            builder.Services.AddOptions<OrdersOptions>().PostConfigure(options => { });
            var assembly = typeof(Program).Assembly;
            builder.Services.AddValidatorsFromAssembly(assembly);
            if (builder.Environment.IsDevelopment())
            {
                builder.Services.AddSingleton<OrdersSeed>();
            }

            var app = builder.Build();
            app.Run();
            """ + Types);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication();

            var app = builder.Build();
            app.Run();
            """ + Types);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
                    builder.Services.Decorate<IOrderRepository, CachedOrderRepository>();
                    builder.Services.AddOptions<OrdersOptions>().PostConfigure(options => { });
                    var assembly = typeof(Program).Assembly;
                    builder.Services.AddValidatorsFromAssembly(assembly);
                    if (builder.Environment.IsDevelopment())
                    {
                        builder.Services.AddSingleton<OrdersSeed>();
                    }
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    [Arguments("app.UseAntiforgery();", "", "app.UseModuleRouting(antiforgery: false);")]
    [Arguments("app.UseApi();", "UseApi", "app.UseModuleRouting(authentication: false, authorization: false);")]
    [Arguments("app.UseApi();\napp.UseAntiforgery();", "UseApi", "app.UseRouting();")]
    // Placed under a condition, it may not run, and then WebApplication would have added it: kept.
    [Arguments("if (args.Length > 0)\n{\n    app.UseAuthentication();\n}", "", "app.UseModuleRouting();")]
    [Arguments("app.UseApi();", "UseApiWhenConfigured", "app.UseModuleRouting(authentication: false);")]
    public async Task TheRoutingTheFixAdds_LeavesToTheProgramWhatItPlacesItself(string pipeline, string ownMiddleware, string routing)
    {
        // An own app.UseApi() that places authentication and authorization, as progcoder's
        // Communication.Api does: what it surely places is the program's too.
        var own = ownMiddleware switch
        {
            "UseApi" => """

                internal static class Pipeline
                {
                    public static IApplicationBuilder UseApi(this IApplicationBuilder app) =>
                        app.UseAuthentication().UseAuthorization();
                }
                """,
            "UseApiWhenConfigured" => """

                internal static class Pipeline
                {
                    public static IApplicationBuilder UseApi(this IApplicationBuilder app)
                    {
                        app.UseAuthentication();
                        if (System.Environment.GetEnvironmentVariable("ORDERS_AUTHORIZATION") is not null)
                        {
                            app.UseAuthorization();
                        }

                        return app;
                    }
                }
                """,
            _ => string.Empty,
        };

        var test = await FixTestAsync();
        test.TestState.Sources.Add($$"""
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {{pipeline}}
            {|MOD0015:app.MapGet("/orders", () => "orders");|}
            app.Run();
            """ + own);

        var usings = routing.StartsWith("app.UseModuleRouting", StringComparison.Ordinal)
            ? "using FusionModules;\nusing Microsoft.AspNetCore.Builder;"
            : "using Microsoft.AspNetCore.Builder;";
        test.FixedState.Sources.Add($$"""
            {{usings}}

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {{routing}}
            {{pipeline}}
            app.Run();
            """ + own);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ARouteGroupMadeFromTheApplication_MovesWithTheMappingsOnIt()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {|MOD0015:var api = app.MapGroup("/api");|}
            api.MapGet("/orders", () => "orders");
            app.Run();
            """);

        test.FixedState.Sources.Add("""
            using FusionModules;
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.UseModuleRouting();
            app.Run();
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        var api = endpoints.MapGroup("/api");
                        api.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ALayeredService_RegisteringOnlyItsProjectReferencesTypes_IsReportedAndConverted()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Ordering.Infrastructure;

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            {|MOD0015:builder.Services.AddDbContext<OrderingContext>();|}
            var app = builder.Build();
            app.Run();
            """);
        await AddSolutionAsync(test);

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            var app = builder.Build();
            app.Run();
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Ordering.Infrastructure;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddDbContext<OrderingContext>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ARouteBuilderFromAnExtensionBlock_MovesWithTheMappingsOnIt()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Asp.Versioning.Builder;
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            {|MOD0015:var orders = app.NewVersionedApi("Orders");|}
            orders.MapGet("/orders", () => "orders");
            app.Run();
            """);

        test.FixedState.Sources.Add("""
            using FusionModules;
            using Microsoft.AspNetCore.Builder;

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.UseModuleRouting();
            app.Run();
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using Asp.Versioning.Builder;
            using FusionModules;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void Configure(IApplicationBuilder app)
                {
                    app.UseEndpoints(endpoints =>
                    {
                        var orders = endpoints.NewVersionedApi("Orders");
                        orders.MapGet("/orders", () => "orders");
                    });
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AReferencedProjectsOwnMethod_ThatShapesTheApplication_Stays_AndAWorkerSharingTheProjectIsNoOtherProgram()
    {
        // The analyzer reads only this project's source, in an IDE as in a build, and takes the
        // referenced AddInfrastructure at its word; the fix reads it, and leaves it in the program.
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Ordering.Infrastructure;

            var builder = WebApplication.CreateBuilder(args);
            {|MOD0015:builder.Services.AddInfrastructure();|}
            builder.Services.AddPersistence();
            var app = builder.Build();
            app.Run();
            """);

        var references = new[] { await AnalyzerTest.Framework.Value, await Web.Value };
        var infrastructure = new ProjectState("Ordering.Infrastructure", LanguageNames.CSharp, "/Infrastructure/", "cs");
        infrastructure.Sources.Add("""
            using Microsoft.Extensions.DependencyInjection;

            namespace Ordering.Infrastructure
            {
                public static class InfrastructureExtensions
                {
                    // Authentication is the whole application's: this one stays.
                    public static IServiceCollection AddInfrastructure(this IServiceCollection services) =>
                        services.AddAuthentication().AddPersistence();

                    public static IServiceCollection AddPersistence(this IServiceCollection services) =>
                        services.AddDbContext<OrderingContext>();
                }

                public sealed class OrderingContext { }
            }
            """);
        infrastructure.AdditionalReferences.AddRange(references);

        // The same service's worker: Host.CreateApplicationBuilder, no WebApplication in sight.
        var worker = new ProjectState("Ordering.Worker", LanguageNames.CSharp, "/Worker/", "cs") { OutputKind = OutputKind.ConsoleApplication };
        worker.Sources.Add("System.Console.WriteLine(typeof(Ordering.Infrastructure.OrderingContext));");
        worker.AdditionalProjectReferences.Add("Ordering.Infrastructure");

        test.TestState.AdditionalProjects.Add("Ordering.Infrastructure", infrastructure);
        test.TestState.AdditionalProjects.Add("Ordering.Worker", worker);
        test.TestState.AdditionalProjectReferences.Add("Ordering.Infrastructure");

        test.FixedState.Sources.Add("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Ordering.Infrastructure;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddInfrastructure();
            var app = builder.Build();
            app.Run();
            """);
        test.FixedState.Sources.Add(("Module.cs", """
            using FusionModules;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Hosting;
            using Ordering.Infrastructure;

            [assembly: HostingStartup(typeof(TestProject.Module))]

            namespace TestProject;

            internal sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddPersistence();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    private static async Task<CSharpCodeFixTest<ProgramConversionAnalyzer, ProgramToModuleCodeFixProvider, DefaultVerifier>> FixTestAsync()
    {
        var test = new CSharpCodeFixTest<ProgramConversionAnalyzer, ProgramToModuleCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        test.TestState.AdditionalReferences.Add(await AnalyzerTest.Framework.Value);
        test.TestState.AdditionalReferences.Add(await Web.Value);
        test.FixedState.OutputKind = OutputKind.ConsoleApplication;
        return test;
    }

    /// <summary>
    /// The service's solution: its own Infrastructure project, and ServiceDefaults with the event bus,
    /// which another program of the solution references too.
    /// </summary>
    private static async Task AddSolutionAsync(CSharpCodeFixTest<ProgramConversionAnalyzer, ProgramToModuleCodeFixProvider, DefaultVerifier> test)
    {
        var references = new[] { await AnalyzerTest.Framework.Value, await Web.Value };

        var infrastructure = new ProjectState("Ordering.Infrastructure", LanguageNames.CSharp, "/Infrastructure/", "cs");
        infrastructure.Sources.Add("""
            namespace Ordering.Infrastructure
            {
                public sealed class OrderingContext { }
            }
            """);
        infrastructure.AdditionalReferences.AddRange(references);

        var serviceDefaults = new ProjectState("ServiceDefaults", LanguageNames.CSharp, "/ServiceDefaults/", "cs");
        serviceDefaults.Sources.Add("""
            namespace Microsoft.Extensions.Hosting
            {
                public sealed class EventBusBuilder
                {
                    public EventBusBuilder AddSubscription<THandler>() => this;
                }

                public static class ServiceDefaultsExtensions
                {
                    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder => builder;

                    public static EventBusBuilder AddEventBus(this IHostApplicationBuilder builder, string connectionName) => new EventBusBuilder();
                }
            }
            """);
        serviceDefaults.AdditionalReferences.AddRange(references);

        var other = new ProjectState("Basket.API", LanguageNames.CSharp, "/Basket/", "cs") { OutputKind = OutputKind.ConsoleApplication };
        other.Sources.Add("Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args).Build().Run();");
        other.AdditionalReferences.AddRange(references);
        other.AdditionalProjectReferences.Add("ServiceDefaults");

        test.TestState.AdditionalProjects.Add("Ordering.Infrastructure", infrastructure);
        test.TestState.AdditionalProjects.Add("ServiceDefaults", serviceDefaults);
        test.TestState.AdditionalProjects.Add("Basket.API", other);
        test.TestState.AdditionalProjectReferences.Add("Ordering.Infrastructure");
        test.TestState.AdditionalProjectReferences.Add("ServiceDefaults");
    }

    private static async Task VerifySilentAsync(string? projectKind, params string[] sources)
    {
        var test = new CSharpAnalyzerTest<ProgramConversionAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };
        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        test.TestState.AdditionalReferences.Add(await AnalyzerTest.Framework.Value);
        test.TestState.AdditionalReferences.Add(await Web.Value);
        foreach (var source in sources)
        {
            test.TestState.Sources.Add(source);
        }

        if (projectKind is not null)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", $"""
                is_global = true
                build_property.FusionModulesProjectKind = {projectKind}
                """));
        }

        await test.RunAsync(CancellationToken.None);
    }
}
