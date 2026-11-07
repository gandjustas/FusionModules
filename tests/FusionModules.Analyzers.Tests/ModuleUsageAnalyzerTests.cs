namespace FusionModules.Analyzers.Tests;

public class ModuleUsageAnalyzerTests
{
    // MOD0008 — a module must not replace the pipeline.

    [Test]
    public Task ConfigureOnTheWebHostBuilder_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using FusionModules;

            sealed class Rogue : ModuleBase
            {
                void Setup(IWebHostBuilder builder) => {|MOD0008:builder.Configure(app => { })|};
            }
            """);

    [Test]
    public Task UseStartup_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using FusionModules;

            sealed class Rogue : ModuleBase
            {
                void Setup(IWebHostBuilder builder) => {|MOD0008:builder.UseStartup<object>()|};
            }
            """);

    [Test]
    public Task ConfigureOnSomethingElse_IsFine() =>
        // The method name is common. Only IWebHostBuilder's counts.
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            sealed class Options
            {
                public void Configure(System.Action<int> setup) { }
                void Use() => Configure(_ => { });
            }
            """);

    // MOD0009 — ModuleBase already registers the module as a startup filter.

    [Test]
    public Task RegisteringTheModuleAsAStartupFilterAgain_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;

            sealed class Eager : ModuleBase
            {
                void Register(IServiceCollection services) => {|MOD0009:services.AddSingleton<IStartupFilter>(this)|};
            }
            """);

    [Test]
    public Task RegisteringAStartupFilterOutsideAModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;

            sealed class NotAModule
            {
                void Register(IServiceCollection services) => services.AddSingleton<IStartupFilter>(null!);
            }
            """);

    [Test]
    public Task RegisteringTheModuleTypeAsAStartupFilter_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;

            sealed class Eager : ModuleBase
            {
                void Register(IServiceCollection services) => {|MOD0009:services.AddSingleton<IStartupFilter, Eager>()|};
            }
            """);

    [Test]
    public Task RegisteringAnotherStartupFilterFromAModule_IsFine() =>
        // A module may well have a startup filter of its own; it is the module itself that
        // ModuleBase has registered already.
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;

            sealed class Timing : IStartupFilter
            {
                public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next) => next;
            }

            sealed class Measured : ModuleBase
            {
                void Register(IServiceCollection services) => services.AddSingleton<IStartupFilter, Timing>();
            }
            """);

    [Test]
    public Task AHostedServiceInAModule_IsFine() =>
        // Whether a worker should run in a topology is a question about topologies, not about
        // the registration.
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using FusionModules;

            sealed class Worker : IHostedService { }

            sealed class Background : ModuleBase
            {
                void Register(IServiceCollection services) => services.AddHostedService<Worker>();
            }
            """);

    [Test]
    public Task OrdinaryServiceRegistrations_AreFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleUsageAnalyzer>("""
            using Microsoft.Extensions.DependencyInjection;
            using FusionModules;

            sealed class Service { }

            sealed class Ordinary : ModuleBase
            {
                void Register(IServiceCollection services) => services.AddSingleton<Service>();
            }
            """);

    // None of these apply outside a module assembly.

    [Test]
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
