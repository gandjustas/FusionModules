using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules;

/// <summary>
/// Routing for a host whose endpoints its modules map.
/// </summary>
/// <remarks>
/// <para>
/// A module maps its endpoints in <see cref="ModuleBase"/>'s <c>Configure</c>, after the host's
/// pipeline: <c>UseEndpoints</c> there needs routing in the host's pipeline, and
/// <c>WebApplication</c> adds routing by itself only while it has endpoints of its own. A host left
/// with none calls <c>UseRouting()</c>.
/// </para>
/// <para>
/// That call moves more than routing. <c>WebApplication</c> also adds authentication,
/// authorization and antiforgery by itself, when their services are registered and the program
/// does not add them — at the very start of the pipeline, before the program's own middleware.
/// With routing implicit that is after routing; with an explicit <c>UseRouting()</c> it is before
/// it, where no endpoint has been chosen yet, and every endpoint that requires authorization fails
/// with a 500 (on .NET 8 and 10; ASP.NET Core 11 does not fail here). This adds them after routing,
/// as <c>WebApplication</c> would have.
/// </para>
/// </remarks>
public static class ModuleRouting
{
    /// <param name="app">The host's application builder.</param>
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds routing, then the authentication, authorization and antiforgery middleware whose
        /// services are registered — what <c>WebApplication</c> adds implicitly, in its order.
        /// </summary>
        /// <param name="authentication">Whether to add authentication; <c>false</c> when the program adds it itself.</param>
        /// <param name="authorization">Whether to add authorization; <c>false</c> when the program adds it itself.</param>
        /// <param name="antiforgery">Whether to add antiforgery; <c>false</c> when the program adds it itself.</param>
        /// <returns>The same builder.</returns>
        /// <remarks>
        /// <c>WebApplication</c> decides about each of the three on its own, so does this: a program
        /// that calls <c>UseAntiforgery()</c> and leaves authentication to the framework passes
        /// <c>antiforgery: false</c> and still gets authentication and authorization after routing.
        /// </remarks>
        public IApplicationBuilder UseModuleRouting(bool authentication = true, bool authorization = true, bool antiforgery = true)
        {
            app.UseRouting();

            var services = app.ApplicationServices.GetService<IServiceProviderIsService>();
            if (authentication && services?.IsService(typeof(IAuthenticationSchemeProvider)) == true)
            {
                app.UseAuthentication();
            }

            if (authorization && services?.IsService(typeof(IAuthorizationHandlerProvider)) == true)
            {
                app.UseAuthorization();
            }

            if (antiforgery && services?.IsService(typeof(IAntiforgery)) == true)
            {
                app.UseAntiforgery();
            }

            return app;
        }
    }
}
