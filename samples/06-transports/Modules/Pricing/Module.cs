using Modulith;
using Pricing.Contracts;
using PricingModule;

[assembly: HostingStartup(typeof(Module))]

namespace PricingModule;

/// <summary>
/// The direct implementation. Loading this module is what turns a network hop into a method call.
/// </summary>
/// <remarks>
/// It maps no endpoints and needs none: everything it offers, it offers through DI. A topology
/// that wants the same logic reachable over HTTP loads <c>PricingApiModule</c> alongside it.
/// </remarks>
sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        // Not TryAdd. This module and PricingClientModule are two answers to the same question, and
        // a topology picks one — loading both means the last name in HOSTINGSTARTUPASSEMBLIES
        // silently decides, which is a collision worth failing on rather than tolerating.
        services.AddScoped<IPricing, Pricing>();
}

sealed class Pricing : IPricing
{
    private static readonly Dictionary<string, decimal> UnitPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hammer"] = 12.50m,
        ["nails"] = 3.00m,
    };

    public Task<Quote> QuoteAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        if (!UnitPrices.TryGetValue(sku, out var unit))
        {
            throw new KeyNotFoundException($"No price for '{sku}'.");
        }

        var gross = unit * quantity;
        var discount = quantity >= 10 ? gross * 0.10m : 0m;

        return Task.FromResult(new Quote(sku, quantity, gross - discount, discount));
    }
}
