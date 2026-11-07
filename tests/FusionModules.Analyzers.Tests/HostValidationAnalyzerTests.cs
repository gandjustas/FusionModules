namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0011: a host that creates its builder and never validates the modules it was asked for.
/// </summary>
public class HostValidationAnalyzerTests
{
    private static Task VerifyAsync(string source) =>
        AnalyzerTest.VerifyHostReferencingModuleAsync<HostValidationAnalyzer>(projectKind: null, AnalyzerTest.GeneratedForAHost, source);

    [Test]
    public Task BuilderThatIsNeverValidated_IsReported() =>
        VerifyAsync("""
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = {|MOD0011:WebApplication.CreateBuilder(args)|};
                }
            }
            """);

    [Test]
    public Task SlimBuilder_IsLeftAlone() =>
        // CreateSlimBuilder runs no hosting startups, so no module loads and there is nothing to
        // validate: asking for ValidateModules there would only fail every topology.
        VerifyAsync("""
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
    public Task BuilderValidatedWhereItIsCreated_IsFine() =>
        VerifyAsync("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args).ValidateModules();
                }
            }
            """);

    [Test]
    public Task BuilderValidatedThroughItsLocal_IsFine() =>
        VerifyAsync("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    builder.ValidateModules();
                }
            }
            """);

    [Test]
    public Task ValidatingOneBuilderDoesNotCoverAnother() =>
        VerifyAsync("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var validated = WebApplication.CreateBuilder(args);
                    var forgotten = {|MOD0011:WebApplication.CreateBuilder(args)|};
                    validated.ValidateModules();
                }
            }
            """);

    [Test]
    public Task BuilderTheMethodReturns_IsLeftToItsCaller() =>
        VerifyAsync("""
            using Microsoft.AspNetCore.Builder;

            static class HostSetup
            {
                public static WebApplicationBuilder Create(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    return builder;
                }
            }
            """);

    [Test]
    public Task GenericHostWithAWebHostThatIsNeverValidated_IsReported() =>
        // GenericWebHostBuilder runs the hosting startups here as it does under WebApplication.
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var host = Host.CreateDefaultBuilder(args)
                        .{|MOD0011:ConfigureWebHostDefaults|}(web => { });
                }
            }
            """);

    [Test]
    public Task ConfigureWebHost_IsCoveredToo() =>
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    Host.CreateDefaultBuilder(args).{|MOD0011:ConfigureWebHost|}(web => { });
                }
            }
            """);

    [Test]
    public Task GenericHostValidatedAnywhereInTheChain_IsFine() =>
        // The check runs when the host is built, so where in the chain it is asked for does not matter.
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var before = Host.CreateDefaultBuilder(args)
                        .ValidateModules()
                        .ConfigureWebHostDefaults(web => { });

                    var after = Host.CreateDefaultBuilder(args)
                        .ConfigureWebHostDefaults(web => { })
                        .ConfigureServices((context, services) => { })
                        .ValidateModules();
                }
            }
            """);

    [Test]
    public Task GenericHostValidatedThroughItsLocal_IsFine() =>
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var host = Host.CreateDefaultBuilder(args);
                    host.ConfigureWebHostDefaults(web => { });
                    host.ValidateModules();
                }
            }
            """);

    [Test]
    public Task GenericHostTheMethodReturns_IsLeftToItsCaller() =>
        // The classic Program.CreateHostBuilder: whoever calls it builds, and validates, the host.
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static IHostBuilder CreateHostBuilder(string[] args) =>
                    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(web => { });
            }
            """);

    [Test]
    public Task GenericHostWithoutAWebHost_IsFine() =>
        // No web host, no hosting startups, no modules.
        VerifyAsync("""
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var host = Host.CreateDefaultBuilder(args).ConfigureServices((context, services) => { });
                }
            }
            """);

    [Test]
    public Task WithoutTheGeneratedCheck_NothingIsReported() =>
        // The list switched off, or a project that is not a host: there is nothing to call.
        AnalyzerTest.VerifyAsync<HostValidationAnalyzer>("""
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
    public Task HostWithoutModules_IsLeftAlone() =>
        // The package added to a web project that has no module yet: nothing to validate, and a
        // warning on the first build would only say the package was a mistake.
        AnalyzerTest.VerifyAsync<HostValidationAnalyzer>(AnalyzerTest.GeneratedForAHost, """
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
        // A test builds hosts of its own, for reasons that are its own.
        AnalyzerTest.VerifyHostReferencingModuleAsync<HostValidationAnalyzer>(
            "Test",
            AnalyzerTest.GeneratedForAHost,
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                }
            }
            """);
}
