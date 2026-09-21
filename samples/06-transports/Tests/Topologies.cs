using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Sample.Transports.Tests;

/// <summary>The edge collapsed: checkout and pricing in one process, resolved through DI.</summary>
[InheritsTests]
public sealed class MergedTopology : PricingContractTests
{
    private readonly ModularWebApplicationFactory _factory = new("CheckoutModule", "PricingModule");

    protected override Task<HttpClient> CheckoutAsync() => Task.FromResult(_factory.CreateClient());

    [After(Test)]
    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// The edge kept: two hosts, the same host project, talking over HTTP.
/// </summary>
/// <remarks>
/// Both are the same image in the same way a deployment means it — one project, two sets of module
/// names. The only thing the test has to arrange is that the client's transport reaches the other
/// host, which is what the named <c>HttpClient</c> is for.
/// </remarks>
[InheritsTests]
public sealed class RemoteTopology : PricingContractTests
{
    private readonly ModularWebApplicationFactory _pricing = new("PricingModule", "PricingApiModule");
    private CheckoutFactory? _checkout;

    protected override Task<HttpClient> CheckoutAsync()
    {
        _checkout ??= new CheckoutFactory(_pricing.Server);
        return Task.FromResult(_checkout.CreateClient());
    }

    [Test]
    public async Task ThePricingHostPublishesTheContractItself(CancellationToken cancellationToken)
    {
        // Which is why this topology is possible at all: PricingApiModule is what makes the
        // implementation reachable from outside, and it is a separate module precisely so that the
        // merged topology above can leave it out.
        var response = await _pricing.CreateClient().GetAsync("/pricing/quote?sku=hammer&quantity=1", cancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheMergedTopologyDoesNotPublishIt(CancellationToken cancellationToken)
    {
        // The negative half, and the one that says the topology is real rather than a label.
        using var merged = new ModularWebApplicationFactory("CheckoutModule", "PricingModule");

        var response = await merged.CreateClient().GetAsync("/pricing/quote?sku=hammer&quantity=1", cancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [After(Test)]
    public void Dispose()
    {
        _checkout?.Dispose();
        _pricing.Dispose();
    }

    /// <summary>
    /// Checkout with the remote client, its transport pointed at the pricing host in this process.
    /// </summary>
    private sealed class CheckoutFactory(TestServer pricing)
        : ModularWebApplicationFactory("CheckoutModule", "PricingClientModule")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The module requires an address, and the handler below decides where it really goes.
            // A real deployment sets this to the pricing service; here the value only has to be a
            // well-formed absolute URI.
            builder.UseSetting("Pricing:BaseAddress", "http://pricing.invalid");

            builder.ConfigureTestServices(services =>
                services.AddHttpClient("pricing").ConfigurePrimaryHttpMessageHandler(pricing.CreateHandler));

            base.ConfigureWebHost(builder);
        }
    }
}
