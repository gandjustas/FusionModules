using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using FusionModules.CodeFixes;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// The fix for CS0672 on a module: the obsolete <c>ConfigureServices</c> moves into the builder overload.
/// </summary>
public class UseBuilderOverloadTests
{
    private const string Header = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;

        [assembly: HostingStartup(typeof(Orders.Module))]

        namespace Orders;

        """;

    private const string HeaderWithHosting = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        [assembly: HostingStartup(typeof(Orders.Module))]

        namespace Orders;

        """;

    [Test]
    public Task TheOverload_IsRewrittenInPlace() =>
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                // Registrations.
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    var section = context.Configuration["Orders"];
                    services.AddSingleton<Module>();
                }
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                // Registrations.
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var section = builder.Configuration["Orders"];
                    builder.Services.AddSingleton<Module>();
                }
            }
            """);

    [Test]
    public Task BothOverloads_BecomeOne_InTheOrderThePackageRanThem() =>
        // mgms's thirteen modules of fifteen: the database in one overload, everything else in the other.
        VerifyAsync(
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                // Its own database.
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.AddRedisClient("cache");

                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    // Controllers and the rest.
            #pragma warning disable CS0618
                    services.AddSingleton<Module>();
            #pragma warning restore CS0618
                }
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                // Its own database.
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.AddRedisClient("cache");
                    // Controllers and the rest.
            #pragma warning disable CS0618
                    builder.Services.AddSingleton<Module>();
            #pragma warning restore CS0618
                }
            }
            """);

    [Test]
    public Task TheWebEnvironment_IsCast_OnlyWhereItsOwnMembersAreUsed() =>
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    var name = context.HostingEnvironment.EnvironmentName;
                    var root = context.HostingEnvironment.WebRootPath;
                }
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var name = builder.Environment.EnvironmentName;
                    var root = ((Microsoft.AspNetCore.Hosting.IWebHostEnvironment)builder.Environment).WebRootPath;
                }
            }
            """);

    [Test]
    public Task AnExpressionBody_BecomesABlock() =>
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services) =>
                    services.AddSingleton<Module>();
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<Module>();
                }
            }
            """);

    [Test]
    public Task TheContextPassedWhole_HasNoCounterpart_AndIsLeftAlone() =>
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services) =>
                    Register(context, services);

                private static void Register(WebHostBuilderContext context, IServiceCollection services) { }
            }
            """,
            fixedSource: null);

    [Test]
    public Task TheBaseCall_TheOverrideTemplateWrites_Goes() =>
        // ModuleBase's overload is empty; the context it passes whole is no reason to give up.
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    {|CS0618:base.ConfigureServices(context, services)|};
                    services.AddSingleton<Module>();
                }
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<Module>();
                }
            }
            """);

    [Test]
    public Task ALambdaParameterCalledBuilder_GetsTheBuilderAnotherName() =>
        VerifyAsync(
            Header + """
            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    System.Func<IServiceCollection, IServiceCollection> configure = builder => builder;
                    configure(services);
                }
            }
            """,
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder hostBuilder)
                {
                    System.Func<IServiceCollection, IServiceCollection> configure = builder => builder;
                    configure(hostBuilder.Services);
                }
            }
            """);

    [Test]
    public Task LocalsOfTheSameName_AreLeftAlone() =>
        // C# keeps a local's name for the whole enclosing scope, so not even a block of its own
        // would let the moved one compile; the merge is a decision about names, and it is yours.
        VerifyAsync(
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var name = builder.Configuration["Orders"];
                }

                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    var name = context.Configuration["Billing"];
                }
            }
            """,
            fixedSource: null);

    [Test]
    public Task AnOverrideThatCanReturnEarly_IsLeftAlone() =>
        // Appended after a return, the old body would no longer run every time, as it used to.
        VerifyAsync(
            HeaderWithHosting + """
            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    if (builder.Configuration["Orders"] is null)
                    {
                        return;
                    }
                }

                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    services.AddSingleton<Module>();
                }
            }
            """,
            fixedSource: null);

    [Test]
    public Task AnExtensionOnTheWebEnvironment_KeepsItsCast() =>
        VerifyAsync(
            Header + """
            static class Environments
            {
                public static bool IsLocal(this IWebHostEnvironment environment) => false;
            }

            sealed class Module : ModuleBase
            {
                protected override void {|CS0672:ConfigureServices|}(WebHostBuilderContext context, IServiceCollection services)
                {
                    var local = context.HostingEnvironment.IsLocal();
                }
            }
            """,
            HeaderWithHosting + """
            static class Environments
            {
                public static bool IsLocal(this IWebHostEnvironment environment) => false;
            }

            sealed class Module : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder)
                {
                    var local = ((Microsoft.AspNetCore.Hosting.IWebHostEnvironment)builder.Environment).IsLocal();
                }
            }
            """);

    private static Task VerifyAsync(string source, string? fixedSource)
    {
        var test = new CSharpCodeFixTest<EmptyDiagnosticAnalyzer, UseBuilderOverloadCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
            CompilerDiagnostics = CompilerDiagnostics.Warnings,
        };

        var framework = new ProjectState("Framework", LanguageNames.CSharp, "/Framework/", "cs");
        // CS0672 is a warning, so warnings are compared — and the stubs have no doc comments.
        framework.Sources.Add("#pragma warning disable CS1591\n" + AnalyzerTest.FrameworkStubs);
        framework.Sources.Add("#pragma warning disable CS1591\n" + AnalyzerTest.ModuleBaseStub);
        test.TestState.AdditionalProjects.Add("Framework", framework);
        test.TestState.AdditionalProjectReferences.Add("Framework");

        test.TestState.Sources.Add(source);
        test.FixedState.Sources.Add(fixedSource ?? source);

        return test.RunAsync(CancellationToken.None);
    }
}
