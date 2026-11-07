namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0006: a host built with a builder that never runs hosting startups, so never loads a module.
/// </summary>
public class SlimBuilderAnalyzerTests
{
    private const string ProjectKind = "build_property.FusionModulesProjectKind";

    private static Task VerifyAsync(string kind, string source) =>
        AnalyzerTest.VerifyHostReferencingModuleAsync<SlimBuilderAnalyzer>(kind, source);

    [Test]
    public Task SlimBuilderInAHostWithoutModules_IsLeftAlone() =>
        // A service that builds slim and loads no module is not wrong, and the package being there
        // does not make it so.
        AnalyzerTest.VerifyWithOptionAsync<SlimBuilderAnalyzer>(ProjectKind, "Host", """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateSlimBuilder(args);
                }
            }
            """);

    [Test]
    public Task SlimBuilderInAHost_IsReported() =>
        VerifyAsync("Host", """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.{|MOD0006:CreateSlimBuilder|}(args);
                }
            }
            """);

    [Test]
    public Task EmptyBuilderInAHost_IsReported() =>
        VerifyAsync("Host", """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.{|MOD0006:CreateEmptyBuilder|}(new WebApplicationOptions { Args = args });
                }
            }
            """);

    [Test]
    public Task UsingStatic_IsReportedOnTheBareName() =>
        VerifyAsync("Host", """
            using static Microsoft.AspNetCore.Builder.WebApplication;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = {|MOD0006:CreateSlimBuilder|}(args);
                }
            }
            """);

    [Test]
    public Task DefaultBuilder_IsFine() =>
        VerifyAsync("Host", """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                }
            }
            """);

    [Test]
    public Task TestProject_IsLeftAlone() =>
        // A test may build a slim host on purpose: it is not where modules are deployed.
        VerifyAsync("Test", """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateSlimBuilder(args);
                }
            }
            """);

    [Test]
    public Task Library_IsLeftAlone() =>
        VerifyAsync("Module", """
            using Microsoft.AspNetCore.Builder;

            static class Helpers
            {
                public static WebApplicationBuilder Create(string[] args) => WebApplication.CreateSlimBuilder(args);
            }
            """);

    [Test]
    public Task WithoutTheRuntimePackage_NothingIsReported() =>
        // An ordinary slim application that happens to compile next to the analyzer.
        AnalyzerTest.VerifyWithoutModuleBaseAsync<SlimBuilderAnalyzer>("""
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateSlimBuilder(args);
                }
            }
            """);
}
