namespace ModularData.Tests;

/// <summary>
/// The module assembly names, as a deployment writes them into HOSTINGSTARTUPASSEMBLIES.
/// </summary>
/// <remarks>
/// Plain strings, in one place. A misspelling here fails startup with a message naming the
/// module — <see cref="ModelCompositionTests.AModuleWhoseDependenciesAreMissingFailsStartup"/>
/// covers the same mechanism.
/// </remarks>
internal static class Modules
{
    public const string OrdersEntities = "Orders.Entities";
    public const string CustomersEntities = "Customers.Entities";
    public const string CustomersApi = "CustomersModule";
    public const string Billing = "BillingModule";

    public static readonly string[] All = [OrdersEntities, CustomersEntities, CustomersApi, Billing];
}
