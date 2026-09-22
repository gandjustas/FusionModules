using FusionModules;
using Pricing.Contracts;
using PricingApiModule;

[assembly: HostingStartup(typeof(Module))]

namespace PricingApiModule;

/// <summary>
/// The contract, published over HTTP. The transport, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately separate from the implementation. Keeping the endpoint in its own module is what
/// lets a topology decide independently whether pricing runs here and whether anyone outside may
/// call it — the two questions the "keep the remote transport when…" list in the skill is really
/// about. It resolves <see cref="IPricing"/> like any other caller, so it does not care which
/// module answered.
/// </remarks>
sealed class Module : ModuleBase
{
    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapGet("/pricing/quote",
            (string sku, int quantity, IPricing pricing, CancellationToken cancellationToken) =>
                pricing.QuoteAsync(sku, quantity, cancellationToken)));
}
