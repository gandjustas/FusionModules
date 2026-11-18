using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FusionModules.Tests;

/// <summary>
/// A host whose only endpoints are its modules', with authorization: what MOD0015's fix leaves
/// behind when it moves every endpoint of a service into its module.
/// </summary>
public class ModuleRoutingTests
{
    [Test]
    public async Task UseModuleRouting_RunsTheImplicitAuthorizationAfterRouting()
    {
        var status = await RequestSecureAsync(app => app.UseModuleRouting());

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task UseModuleRouting_LeavesOutWhatTheProgramPlacesItself()
    {
        // The program places authorization itself; authentication, left to the framework, still
        // comes after routing.
        var status = await RequestSecureAsync(app =>
        {
            app.UseModuleRouting(authorization: false);
            app.UseAuthorization();
        });

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task APlainUseRouting_LeavesTheImplicitAuthorizationBeforeIt_AndTheEndpointFails()
    {
        // Why UseModuleRouting exists: WebApplication puts the authorization it adds by itself
        // before the program's pipeline, so before this routing, where no endpoint is chosen yet.
        var status = await RequestSecureAsync(app => app.UseRouting());

#if NET11_0_OR_GREATER
        // ASP.NET Core 11 no longer fails here: the endpoint is reached and refused as it should be.
        // UseModuleRouting stays correct on it, and is still needed for net8.0 and net10.0.
        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
#else
        await Assert.That(status).IsEqualTo(HttpStatusCode.InternalServerError);
#endif
    }

    private static async Task<HttpStatusCode> RequestSecureAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(NoUserHandler.Name).AddScheme<AuthenticationSchemeOptions, NoUserHandler>(NoUserHandler.Name, null);
        builder.Services.AddAuthorization();
        ((IHostingStartup)new SecureModule()).Configure(builder.WebHost);

        await using var app = builder.Build();
        configure(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient();
            using var response = await client.GetAsync(new Uri(new Uri(address), "/secure"));
            return response.StatusCode;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class SecureModule : ModuleBase
    {
        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints => endpoints.MapGet("/secure", () => "secret").RequireAuthorization());
    }

    /// <summary>Authenticates nobody, and challenges with a 401.</summary>
    private sealed class NoUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "NoUser";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
