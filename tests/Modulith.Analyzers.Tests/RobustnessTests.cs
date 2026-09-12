namespace Modulith.Analyzers.Tests;

/// <summary>
/// An analyzer that throws takes the whole build with it, and the inputs that make one throw are
/// exactly the malformed ones a user is staring at when they need the build to work.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public Task EmptyCompilation_IsFine() =>
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>(string.Empty);

    [Fact]
    public Task CompilationWithoutAspNetCore_IsFine() =>
        // GetTypeByMetadataName returns null for every lookup. The rules must go quiet, not crash.
        AnalyzerTest.VerifyWithoutAspNetCoreAsync<ModuleAnalyzer>("""
            public class Ordinary { }
            """);

    [Fact]
    public Task HostAnalyzerWithoutAspNetCore_IsFine() =>
        AnalyzerTest.VerifyWithoutAspNetCoreAsync<HostAnalyzer>("""
            public class Ordinary { }
            """);

    [Fact]
    public Task HostingStartupWithoutArguments_IsFine() =>
        // ConstructorArguments.Single() threw here in the demo analyzer.
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;

            [assembly: {|CS7036:HostingStartup|}]
            """);

    [Fact]
    public Task HostingStartupNamingAnUnknownType_IsFine() =>
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof({|CS0246:DoesNotExist|}))]
            """);
}
