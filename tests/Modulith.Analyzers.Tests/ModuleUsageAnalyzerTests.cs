namespace Modulith.Analyzers.Tests;

public class ModuleUsageAnalyzerTests
{
    // MOD0006 — a module must not replace the pipeline.

    [Fact]
    public Task ConfigureOnTheWebHostBuilder_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Modulith;

            sealed class Rogue : ModuleBase
            {
                void Setup(IWebHostBuilder builder) => {|MOD0006:builder.Configure(app => { })|};
            }
            """);

    [Fact]
    public Task UseStartup_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Modulith;

            sealed class Rogue : ModuleBase
            {
                void Setup(IWebHostBuilder builder) => {|MOD0006:builder.UseStartup<object>()|};
            }
            """);

    [Fact]
    public Task ConfigureOnSomethingElse_IsFine() =>
        // The method name is common. Only IWebHostBuilder's counts.
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            sealed class Options
            {
                public void Configure(System.Action<int> setup) { }
                void Use() => Configure(_ => { });
            }
            """);

    // MOD0007 — ModuleBase already registers the module as a startup filter.

    [Fact]
    public Task RegisteringTheModuleAsAStartupFilterAgain_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Modulith;

            sealed class Eager : ModuleBase
            {
                void Register(IServiceCollection services) => {|MOD0007:services.AddSingleton<IStartupFilter>(this)|};
            }
            """);

    [Fact]
    public Task RegisteringAStartupFilterOutsideAModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            sealed class NotAModule
            {
                void Register(IServiceCollection services) => services.AddSingleton<IStartupFilter>(null!);
            }
            """);

    // MOD0008 — a hosted service in a module runs in more places than it used to.

    [Fact]
    public Task AHostedServiceInAModule_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Modulith;

            sealed class Worker : IHostedService { }

            sealed class Background : ModuleBase
            {
                void Register(IServiceCollection services) => {|MOD0008:services.AddHostedService<Worker>()|};
            }
            """);

    [Fact]
    public Task OrdinaryServiceRegistrations_AreFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;
            using Modulith;

            sealed class Service { }

            sealed class Ordinary : ModuleBase
            {
                void Register(IServiceCollection services) => services.AddSingleton<Service>();
            }
            """);

    // None of these apply outside a module assembly.

    [Fact]
    public Task TheSameCodeInAHost_IsFine() =>
        AnalyzerTest.VerifyHostAsync<ModuleUsageAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            sealed class Worker : IHostedService { }

            class Host
            {
                static void Main() { }

                static void Register(IServiceCollection services) => services.AddHostedService<Worker>();
            }
            """);
}
