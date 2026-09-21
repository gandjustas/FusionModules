namespace Pricing.Contracts;

/// <summary>
/// The contract, and the point of the whole phase: it leaves both services.
/// </summary>
/// <remarks>
/// <para>
/// A plain library with no <c>[HostingStartup]</c> — so it is not a module, MOD0001 does not apply
/// to it, and both the caller and whichever implementation is loaded may reference it without
/// either depending on the other's deployment.
/// </para>
/// <para>
/// Which implementation the caller gets is a topology decision, made by
/// <c>HOSTINGSTARTUPASSEMBLIES</c>. That is what makes the merge reversible: an edge can be put
/// back on the network without touching a line of the caller.
/// </para>
/// </remarks>
public interface IPricing
{
    Task<Quote> QuoteAsync(string sku, int quantity, CancellationToken cancellationToken);
}

/// <summary>A price, and what it was worked out from.</summary>
public sealed record Quote(string Sku, int Quantity, decimal Total, decimal Discount);
