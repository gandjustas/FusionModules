using CheckoutModule;
using FusionModules;
using Pricing.Contracts;

[assembly: HostingStartup(typeof(Module))]

namespace CheckoutModule;

/// <summary>
/// The caller. It references the contract and nothing else, which is the whole of its side of the
/// bargain — and the reason its code is identical in both topologies.
/// </summary>
/// <remarks>
/// Note what is absent. There is no retry and no circuit breaker here, because in the merged
/// topology they would wrap a method call: a retry re-runs a local method and a breaker around one
/// does nothing. Whatever resilience the edge needs belongs with the transport, which is to say in
/// PricingClientModule, where the I/O actually is.
/// </remarks>
sealed class Module : ModuleBase
{
    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapGet("/checkout/quote",
            async (string sku, int quantity, IPricing pricing, CancellationToken cancellationToken) =>
            {
                var quote = await pricing.QuoteAsync(sku, quantity, cancellationToken);
                return Results.Ok(quote);
            }));
}
