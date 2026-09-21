using System.Net.Http.Json;
using Pricing.Contracts;

namespace Sample.Transports.Tests;

/// <summary>
/// One set of expectations for the contract, run against both of its implementations.
/// </summary>
/// <remarks>
/// This is the property the pattern gives you for free, and the gate the skill asks for: the caller
/// is identical in both topologies, so the suite that proves the edge still behaves has to be
/// identical too. A difference between the rows below would be a behaviour change the merge
/// introduced, which is exactly what nobody notices otherwise.
/// </remarks>
public abstract class PricingContractTests
{
    /// <summary>A checkout that can reach pricing, however it reaches it.</summary>
    protected abstract Task<HttpClient> CheckoutAsync();

    [Test]
    public async Task QuotesAtTheUnitPrice(CancellationToken cancellationToken)
    {
        var client = await CheckoutAsync();

        var quote = await client.GetFromJsonAsync<Quote>("/checkout/quote?sku=hammer&quantity=2", cancellationToken);

        await Assert.That(quote!.Total).IsEqualTo(25.00m);
        await Assert.That(quote.Discount).IsEqualTo(0m);
    }

    [Test]
    public async Task DiscountsFromTenUp(CancellationToken cancellationToken)
    {
        var client = await CheckoutAsync();

        var quote = await client.GetFromJsonAsync<Quote>("/checkout/quote?sku=nails&quantity=10", cancellationToken);

        await Assert.That(quote!.Discount).IsEqualTo(3.00m);
        await Assert.That(quote.Total).IsEqualTo(27.00m);
    }
}
