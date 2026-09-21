using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Modulith.Analyzers.Tests;

/// <summary>
/// Runs an analyzer over a snippet.
/// </summary>
/// <remarks>
/// Most tests compile against <see cref="FrameworkStubs"/> rather than real reference assemblies:
/// the rules only ever ask about a handful of types by metadata name, and stub declarations make
/// the suite hermetic, fast and indifferent to which target packs happen to be installed.
/// The stubs live in their own project, as the real framework does — putting them in the
/// compilation under test would make MOD0001 fire on ControllerBase itself.
/// </remarks>
internal static class AnalyzerTest
{
    private const string FrameworkProject = "Framework";
    private const string ModuleProject = "OrdersModule";

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
            public interface IHostingStartup { }

            public interface IStartupFilter { }

            public interface IWebHostBuilder { }

            public static class WebHostBuilderExtensions
            {
                public static IWebHostBuilder Configure(this IWebHostBuilder builder, System.Action<object> configure) => builder;
                public static IWebHostBuilder UseStartup<TStartup>(this IWebHostBuilder builder) => builder;
            }

            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class HostingStartupAttribute : System.Attribute
            {
                public HostingStartupAttribute(System.Type hostingStartupType) { }
            }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class ViewComponentAttribute : System.Attribute { }
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

        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostedService { }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }

            public static class ServiceCollectionExtensions
            {
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services) => services;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService instance) => services;
                public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services)
                    where THostedService : Microsoft.Extensions.Hosting.IHostedService => services;
            }
        }
        """;

    /// <summary>The runtime package's one public type, as a stub.</summary>
    public const string ModuleBaseStub = """
        namespace Modulith
        {
            public abstract class ModuleBase : Microsoft.AspNetCore.Hosting.IHostingStartup, Microsoft.AspNetCore.Hosting.IStartupFilter { }
        }
        """;

    /// <summary>
    /// A registered, well-formed module. Present in most snippets so the assembly counts as a
    /// module assembly, which is what switches MOD0001 on.
    /// </summary>
    public const string RegisteredModule = """
        using Microsoft.AspNetCore.Hosting;
        using Modulith;

        [assembly: HostingStartup(typeof(TestModule))]

        sealed class TestModule : ModuleBase { }
        """;

    public static Task VerifyAsync<TAnalyzer>(params string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        Build<TAnalyzer>(withModuleBase: true, sources).RunAsync(CancellationToken.None);

    /// <summary>Verifies a snippet in an assembly that is a module: a registered module, then the code.</summary>
    public static Task VerifyModuleAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        VerifyAsync<TAnalyzer>(RegisteredModule, source);

    /// <summary>Verifies a snippet compiled without the runtime package present.</summary>
    public static Task VerifyWithoutModuleBaseAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        Build<TAnalyzer>(withModuleBase: false, [source]).RunAsync(CancellationToken.None);

    /// <summary>Verifies a snippet compiled with no ASP.NET Core types in scope at all.</summary>
    public static Task VerifyWithoutAspNetCoreAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };
        test.TestState.Sources.Add(source);

        return test.RunAsync(CancellationToken.None);
    }

    public static Task VerifyWithOptionAsync<TAnalyzer>(string key, string value, string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = Build<TAnalyzer>(withModuleBase: true, [RegisteredModule, source]);
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", $"""
            is_global = true
            {key} = {value}
            """));

        return test.RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// Verifies host-side rules: an executable that references a real module assembly.
    /// </summary>
    /// <param name="source">The host's own code. Must contain an entry point.</param>
    /// <param name="expected">Expected diagnostics. MOD0003 and MOD0004 have no location, so
    /// they cannot be written as markup.</param>
    public static Task VerifyHostAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        const string moduleAssembly = """
            using Microsoft.AspNetCore.Hosting;
            using Modulith;

            [assembly: HostingStartup(typeof(OrdersModule))]

            sealed class OrdersModule : ModuleBase { }

            // A module should not expose this (MOD0001 in its own build); it is here so the host
            // has something to illegitimately reach for.
            public class OrderLeak { }
            """;

        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };

        // Three assemblies, as in a real solution. The framework has to be separate from the
        // module: with ApplicationPartAttribute inside the module assembly, a host that merely
        // declares the attribute would also count as using a module type.
        var framework = new ProjectState(FrameworkProject, LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(FrameworkStubs);
        framework.Sources.Add(ModuleBaseStub);

        var module = new ProjectState(ModuleProject, LanguageNames.CSharp, "/Module/", "cs");
        module.Sources.Add(moduleAssembly);
        module.AdditionalProjectReferences.Add(FrameworkProject);

        test.TestState.AdditionalProjects.Add(FrameworkProject, framework);
        test.TestState.AdditionalProjects.Add(ModuleProject, module);
        test.TestState.AdditionalProjectReferences.Add(FrameworkProject);
        test.TestState.AdditionalProjectReferences.Add(ModuleProject);
        test.TestState.OutputKind = OutputKind.ConsoleApplication;
        test.TestState.Sources.Add(source);
        test.TestState.ExpectedDiagnostics.AddRange(expected);

        return test.RunAsync(CancellationToken.None);
    }

    private static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> Build<TAnalyzer>(bool withModuleBase, string[] sources)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> { ReferenceAssemblies = ReferenceAssemblies.Default };

        var framework = new ProjectState(FrameworkProject, LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(FrameworkStubs);
        if (withModuleBase)
        {
            framework.Sources.Add(ModuleBaseStub);
        }

        test.TestState.AdditionalProjects.Add(FrameworkProject, framework);
        test.TestState.AdditionalProjectReferences.Add(FrameworkProject);

        foreach (var source in sources)
        {
            test.TestState.Sources.Add(source);
        }

        return test;
    }
}
