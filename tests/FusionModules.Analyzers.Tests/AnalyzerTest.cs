using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// Runs an analyzer over a snippet.
/// </summary>
/// <remarks>
/// Most tests compile against <see cref="FrameworkStubs"/> rather than real reference assemblies:
/// the rules only ever ask about a handful of types by metadata name, and stub declarations make
/// the suite hermetic, fast and indifferent to which target packs happen to be installed.
/// The stubs live in their own assembly, as the real framework does — putting them in the
/// compilation under test would make MOD0001 fire on ControllerBase itself. That assembly is
/// compiled once per test run and handed to every test as a metadata reference, rather than
/// recompiled as a project by each of them.
/// </remarks>
internal static class AnalyzerTest
{
    private const string ModuleProject = "OrdersModule";

    // Lazy rather than ??=: TUnit runs tests in parallel, and each would otherwise resolve and
    // compile the same references for itself.
    /// <summary>The reference assemblies every compilation in the suite starts from.</summary>
    public static readonly Lazy<Task<ImmutableArray<MetadataReference>>> BaseReferences = new(
        () => ReferenceAssemblies.Default.ResolveAsync(LanguageNames.CSharp, CancellationToken.None));

    /// <summary>The framework stubs and the <c>ModuleBase</c> stub, as one compiled assembly.</summary>
    public static readonly Lazy<Task<MetadataReference>> Framework = new(async () =>
        Compile("Framework", [FrameworkStubs, ModuleBaseStub], await BaseReferences.Value));

    private static readonly Lazy<Task<MetadataReference>> FrameworkWithoutModuleBase = new(async () =>
        Compile("Framework", [FrameworkStubs], await BaseReferences.Value));

    /// <summary>
    /// Stand-ins for the framework types the rules look up by metadata name.
    /// </summary>
    public const string FrameworkStubs = """
        namespace System.Runtime.CompilerServices
        {
            // Records need it, and the default reference assemblies predate it.
            public static class IsExternalInit { }
        }

        namespace Microsoft.AspNetCore.Hosting
        {
            public interface IHostingStartup
            {
                void Configure(IWebHostBuilder builder);
            }

            public interface IStartupFilter
            {
                System.Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(System.Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next);
            }

            public interface IWebHostBuilder
            {
                IWebHostBuilder ConfigureServices(System.Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> configureServices);
                IWebHostBuilder ConfigureServices(System.Action<WebHostBuilderContext, Microsoft.Extensions.DependencyInjection.IServiceCollection> configureServices);
                IWebHostBuilder UseSetting(string key, string value);
            }

            public static class WebHostBuilderExtensions
            {
                public static IWebHostBuilder Configure(this IWebHostBuilder builder, System.Action<object> configure) => builder;
                public static IWebHostBuilder UseStartup<TStartup>(this IWebHostBuilder builder) => builder;
            }

            public static class WebHostDefaults
            {
                public const string HostingStartupAssembliesKey = "hostingStartupAssemblies";
            }

            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class HostingStartupAttribute : System.Attribute
            {
                public HostingStartupAttribute(System.Type hostingStartupType) { }
            }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public interface IApplicationBuilder { }

            public sealed class WebApplicationBuilder : Microsoft.Extensions.Hosting.IHostApplicationBuilder
            {
                public Microsoft.Extensions.DependencyInjection.IServiceCollection Services => null;
                public Microsoft.Extensions.Configuration.IConfigurationManager Configuration => null;
                public Microsoft.Extensions.Hosting.IHostEnvironment Environment => null;
            }

            public sealed class WebApplicationOptions
            {
                public string[] Args { get; set; }
            }

            public sealed class WebApplication
            {
                public static WebApplicationBuilder CreateBuilder() => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateBuilder(string[] args) => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateBuilder(WebApplicationOptions options) => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateSlimBuilder() => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateSlimBuilder(string[] args) => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateSlimBuilder(WebApplicationOptions options) => new WebApplicationBuilder();
                public static WebApplicationBuilder CreateEmptyBuilder(WebApplicationOptions options) => new WebApplicationBuilder();
            }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class ViewComponentAttribute : System.Attribute { }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class NonControllerAttribute : System.Attribute { }
        }

        namespace Microsoft.AspNetCore.Razor.Hosting
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class RazorCompiledItemAttribute : System.Attribute
            {
                public RazorCompiledItemAttribute(System.Type type, string kind, string identifier) { }
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IMvcBuilder
            {
                IServiceCollection Services { get; }
            }

            public static class MvcServiceCollectionExtensions
            {
                public static IMvcBuilder AddControllers(this IServiceCollection services) => null;
                public static IMvcBuilder AddControllersWithViews(this IServiceCollection services) => null;
                public static IMvcBuilder AddRazorPages(this IServiceCollection services) => null;
            }

            public static class MvcCoreMvcBuilderExtensions
            {
                public static IMvcBuilder AddApplicationPart(this IMvcBuilder builder, System.Reflection.Assembly assembly) => builder;
                public static IMvcBuilder AddJsonOptions(this IMvcBuilder builder, System.Action<object> configure) => builder;
                public static IMvcBuilder ConfigureApplicationPartManager(this IMvcBuilder builder, System.Action<object> configure) => builder;
            }
        }

        namespace Microsoft.AspNetCore.Mvc.RazorPages
        {
            public abstract class PageModel { }
        }

        namespace Microsoft.AspNetCore.Mvc.ApplicationParts
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class ApplicationPartAttribute : System.Attribute
            {
                public ApplicationPartAttribute(string assemblyName) { }
            }
        }

        namespace Microsoft.AspNetCore.Razor.TagHelpers
        {
            public interface ITagHelper { }
        }

        namespace Microsoft.AspNetCore.SignalR
        {
            public abstract class Hub { }

            // The constraint is SignalR's own: the stub must not model a laxer framework.
            public abstract class Hub<T> : Hub where T : class { }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            public interface IEntityTypeConfiguration<T> where T : class { }
        }

        namespace Microsoft.Extensions.Configuration
        {
            public interface IConfiguration
            {
                string this[string key] { get; set; }
            }

            public interface IConfigurationManager : IConfiguration { }
        }

        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostedService { }

            public interface IHostApplicationBuilder
            {
                Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; }
                Microsoft.Extensions.Configuration.IConfigurationManager Configuration { get; }
                IHostEnvironment Environment { get; }
            }

            public class HostBuilderContext
            {
                public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; set; }
            }

            public interface IHostBuilder
            {
                IHostBuilder ConfigureServices(System.Action<HostBuilderContext, Microsoft.Extensions.DependencyInjection.IServiceCollection> configureDelegate);
            }

            public static class Host
            {
                public static IHostBuilder CreateDefaultBuilder(string[] args) => null;
            }

            public static class GenericHostBuilderExtensions
            {
                public static IHostBuilder ConfigureWebHostDefaults(this IHostBuilder builder, System.Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure) => builder;
            }

            public static class GenericHostWebHostBuilderExtensions
            {
                public static IHostBuilder ConfigureWebHost(this IHostBuilder builder, System.Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure) => builder;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }

            public static class ServiceCollectionExtensions
            {
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services) => services;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService instance) => services;
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
                public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services)
                    where THostedService : Microsoft.Extensions.Hosting.IHostedService => services;
            }
        }

        // What a module's ConfigureServices overloads are written against.
        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostEnvironment
            {
                string EnvironmentName { get; }
            }

            public static class HostApplicationBuilderStubExtensions
            {
                public static IHostApplicationBuilder AddRedisClient(this IHostApplicationBuilder builder, string connectionName) => builder;
            }
        }

        namespace Microsoft.AspNetCore.Hosting
        {
            public interface IWebHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
            {
                string WebRootPath { get; }
            }

            public class WebHostBuilderContext
            {
                public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; set; }
                public IWebHostEnvironment HostingEnvironment { get; set; }
            }
        }
        """;

    /// <summary>The runtime package's one public type, as a stub.</summary>
    public const string ModuleBaseStub = """
        namespace FusionModules
        {
            public abstract class ModuleBase : Microsoft.AspNetCore.Hosting.IHostingStartup, Microsoft.AspNetCore.Hosting.IStartupFilter
            {
                public static System.Collections.Generic.IReadOnlyList<System.Reflection.Assembly> GetLoadedModules(
                    Microsoft.Extensions.Configuration.IConfiguration configuration) => new System.Reflection.Assembly[0];

                [System.Obsolete("Override ConfigureServices(IHostApplicationBuilder) instead.")]
                protected virtual void ConfigureServices(Microsoft.AspNetCore.Hosting.WebHostBuilderContext context, Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }

                protected virtual void ConfigureServices(Microsoft.Extensions.Hosting.IHostApplicationBuilder builder) { }

                protected virtual void Configure(Microsoft.AspNetCore.Builder.IApplicationBuilder app) { }

                void Microsoft.AspNetCore.Hosting.IHostingStartup.Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) { }

                System.Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Microsoft.AspNetCore.Hosting.IStartupFilter.Configure(
                    System.Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => next;
            }

            public static class ModuleDiscovery
            {
                public static Microsoft.Extensions.DependencyInjection.IMvcBuilder AllowInternalControllers(this Microsoft.Extensions.DependencyInjection.IMvcBuilder builder) => builder;
            }
        }
        """;

    /// <summary>
    /// What <c>KnownModulesGenerator</c> writes into a host: the list, empty here, and the
    /// <c>ValidateModules</c> extension, taken from the generator itself.
    /// </summary>
    public static readonly string GeneratedForAHost = """
        namespace FusionModules
        {
            internal static class KnownModules
            {
                public static string[] Names => new string[0];
            }
        }
        """ + KnownModulesGenerator.Validation;

    /// <summary>
    /// A registered, well-formed module. Present in most snippets so the assembly counts as a
    /// module assembly, which is what switches MOD0001 on.
    /// </summary>
    public const string RegisteredModule = """
        using Microsoft.AspNetCore.Hosting;
        using FusionModules;

        [assembly: HostingStartup(typeof(TestModule))]

        sealed class TestModule : ModuleBase { }
        """;

    /// <summary>Compiles sources into an in-memory assembly, failing loudly when they do not compile.</summary>
    public static MetadataReference Compile(string name, IEnumerable<string> sources, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            throw new InvalidOperationException($"{name} did not compile: {string.Join("; ", result.Diagnostics)}");
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>
    /// Points a test's primary project at the framework stubs, with the registered module and
    /// the given sources in it. The code fix tests share it.
    /// </summary>
    public static async Task ArrangeAsync(SolutionState state, bool withModuleBase, IEnumerable<string> sources)
    {
        state.AdditionalReferences.Add(await (withModuleBase ? Framework : FrameworkWithoutModuleBase).Value);

        foreach (var source in sources)
        {
            state.Sources.Add(source);
        }
    }

    public static async Task VerifyAsync<TAnalyzer>(params string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        await (await BuildAsync<TAnalyzer>(withModuleBase: true, sources)).RunAsync(CancellationToken.None);

    /// <summary>Verifies a snippet in an assembly that is a module: a registered module, then the code.</summary>
    public static Task VerifyModuleAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        VerifyAsync<TAnalyzer>(RegisteredModule, source);

    /// <summary>Verifies a snippet compiled without the runtime package present.</summary>
    public static async Task VerifyWithoutModuleBaseAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        await (await BuildAsync<TAnalyzer>(withModuleBase: false, [source])).RunAsync(CancellationToken.None);

    /// <summary>Verifies a snippet compiled with no ASP.NET Core types in scope at all.</summary>
    public static Task VerifyWithoutAspNetCoreAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };
        test.TestState.Sources.Add(source);

        return test.RunAsync(CancellationToken.None);
    }

    public static async Task VerifyWithOptionAsync<TAnalyzer>(string key, string value, params string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = await BuildAsync<TAnalyzer>(withModuleBase: true, [RegisteredModule, .. sources]);
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", $"""
            is_global = true
            {key} = {value}
            """));

        await test.RunAsync(CancellationToken.None);
    }

    /// <summary>The module the host-side tests reference: a registered module and a type to reach for.</summary>
    private const string ReferencedModule = """
        using Microsoft.AspNetCore.Hosting;
        using FusionModules;

        [assembly: HostingStartup(typeof(OrdersModule))]

        sealed class OrdersModule : ModuleBase { }

        // A module should not expose this (MOD0001 in its own build); it is here so the host
        // has something to illegitimately reach for.
        public class OrderLeak { }
        """;

    /// <summary>
    /// Verifies host-side rules: an executable that references a real module assembly.
    /// </summary>
    /// <param name="source">The host's own code. Must contain an entry point.</param>
    /// <param name="expected">Expected diagnostics. MOD0004 and MOD0007 have no location, so
    /// they cannot be written as markup.</param>
    public static Task VerifyHostAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        VerifyReferencingModuleAsync<TAnalyzer>(source, OutputKind.ConsoleApplication, moduleSource: null, ordinaryBuild: false, expected);

    /// <summary>Verifies a library that is not a module and references a real module assembly.</summary>
    public static Task VerifyLibraryAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        VerifyReferencingModuleAsync<TAnalyzer>(source, OutputKind.DynamicallyLinkedLibrary, moduleSource: null, ordinaryBuild: false, expected);

    /// <summary>
    /// Verifies a host as an ordinary build compiles it — without <c>/doc</c>, so with
    /// <c>DocumentationMode.None</c> — next to a module with extra code of its own. The testing
    /// library parses with <c>DocumentationMode.Diagnose</c> otherwise, under which the compiler
    /// tracks imports and over-reporting of used references cannot show.
    /// </summary>
    public static Task VerifyHostInOrdinaryBuildAsync<TAnalyzer>(string source, string moduleSource, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        VerifyReferencingModuleAsync<TAnalyzer>(source, OutputKind.ConsoleApplication, moduleSource, ordinaryBuild: true, expected);

    /// <summary>The module of <see cref="ReferencedModule"/>, compiled once, as a metadata reference.</summary>
    public static readonly Lazy<Task<MetadataReference>> ModuleReference = new(async () =>
        Compile(ModuleProject, [ReferencedModule], [.. await BaseReferences.Value, await Framework.Value]));

    /// <summary>
    /// Verifies a host that references a module, as a host with modules does: the sources, the
    /// module as a compiled reference and, when given, the project kind MSBuild would set.
    /// </summary>
    public static async Task VerifyHostReferencingModuleAsync<TAnalyzer>(string? projectKind, params string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = await BuildAsync<TAnalyzer>(withModuleBase: true, sources);
        test.TestState.AdditionalReferences.Add(await ModuleReference.Value);
        if (sources.Any(source => source.Contains("static void Main", StringComparison.Ordinal)))
        {
            // A program, so that a project kind left to the entry point has one to find.
            test.TestState.OutputKind = OutputKind.ConsoleApplication;
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

    /// <summary>
    /// Verifies a host that uses a plain library, which in turn uses the module. Both are
    /// compiled ahead, as the host sees them: metadata references.
    /// </summary>
    public static async Task VerifyHostWithLibraryAsync<TAnalyzer>(string librarySource, string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        ImmutableArray<MetadataReference> references = [.. await BaseReferences.Value, await Framework.Value];
        var module = Compile(ModuleProject, [ReferencedModule], references);
        var library = Compile("SharedLibrary", [librarySource], [.. references, module]);

        var test = await BuildAsync<TAnalyzer>(withModuleBase: true, [source]);
        test.TestState.AdditionalReferences.Add(module);
        test.TestState.AdditionalReferences.Add(library);
        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        test.TestState.ExpectedDiagnostics.AddRange(expected);

        await test.RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// Verifies a host built with every warning as an error, as under <c>TreatWarningsAsErrors</c>.
    /// Compiler diagnostics are not compared: the point is what the analyzer reports beside them.
    /// </summary>
    public static async Task VerifyHostWithWarningsAsErrorsAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = await BuildAsync<TAnalyzer>(withModuleBase: true, [source]);
        test.TestState.AdditionalReferences.Add(await ModuleReference.Value);
        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        test.CompilerDiagnostics = CompilerDiagnostics.None;
        test.SolutionTransforms.Add((solution, projectId) => solution.WithProjectCompilationOptions(
            projectId, solution.GetProject(projectId)!.CompilationOptions!.WithGeneralDiagnosticOption(ReportDiagnostic.Error)));

        await test.RunAsync(CancellationToken.None);
    }

    private static async Task VerifyReferencingModuleAsync<TAnalyzer>(
        string source, OutputKind outputKind, string? moduleSource, bool ordinaryBuild, DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        // Three assemblies, as in a real solution. The framework has to be separate from the
        // module: with ApplicationPartAttribute inside the module assembly, a host that merely
        // declares the attribute would also count as using a module type.
        var test = await BuildAsync<TAnalyzer>(withModuleBase: true, [source]);

        var module = new ProjectState(ModuleProject, LanguageNames.CSharp, "/Module/", "cs");
        module.Sources.Add(ReferencedModule);
        if (moduleSource is not null)
        {
            module.Sources.Add(moduleSource);
        }

        module.AdditionalReferences.Add(await Framework.Value);

        test.TestState.AdditionalProjects.Add(ModuleProject, module);
        test.TestState.AdditionalProjectReferences.Add(ModuleProject);
        test.TestState.OutputKind = outputKind;
        test.TestState.ExpectedDiagnostics.AddRange(expected);

        if (ordinaryBuild)
        {
            test.SolutionTransforms.Add((solution, projectId) =>
            {
                foreach (var project in solution.Projects)
                {
                    solution = solution.WithProjectParseOptions(
                        project.Id, ((CSharpParseOptions)project.ParseOptions!).WithDocumentationMode(DocumentationMode.None));
                }

                return solution;
            });
        }

        await test.RunAsync(CancellationToken.None);
    }

    private static async Task<CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>> BuildAsync<TAnalyzer>(bool withModuleBase, string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };
        await ArrangeAsync(test.TestState, withModuleBase, sources);

        return test;
    }
}
