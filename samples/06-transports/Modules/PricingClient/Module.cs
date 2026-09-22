using System.Net.Http.Json;
using FusionModules;
using Pricing.Contracts;
using PricingClientModule;

[assembly: HostingStartup(typeof(Module))]

namespace PricingClientModule;

/// <summary>
/// The old transport, kept behind the same interface.
/// </summary>
/// <remarks>
/// This is the third step of the pattern and the one that is easy to skip. Moving the
/// <c>HttpClient</c> out of the caller and into a module of its own is what makes the edge a
/// deployment choice instead of a code change — and it is what you want on hand the first time the
/// merge turns out to have been wrong for one particular edge.
/// </remarks>
sealed class Module : ModuleBase
{
    public const string HttpClientName = "pricing";

    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        var address = context.Configuration["Pricing:BaseAddress"]
                      ?? throw new InvalidOperationException(
                          "Pricing:BaseAddress is required when PricingClientModule is loaded.");

        // A named client rather than a typed one, so a test — or a resilience policy — can
        // reconfigure the transport without naming this module's internal types.
        services.AddHttpClient(HttpClientName, client => client.BaseAddress = new Uri(address));

        services.AddScoped<IPricing, HttpPricing>();
    }
}

sealed class HttpPricing(IHttpClientFactory clients) : IPricing
{
    public async Task<Quote> QuoteAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(Module.HttpClientName);

        var quote = await client.GetFromJsonAsync<Quote>(
            $"/pricing/quote?sku={Uri.EscapeDataString(sku)}&quantity={quantity}", cancellationToken);

        return quote ?? throw new InvalidOperationException($"Pricing returned nothing for '{sku}'.");
    }
}
