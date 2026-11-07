namespace FusionModules.Tests.Shared;

/// <summary>
/// How a type may be named in the package's settings.
/// </summary>
/// <remarks>
/// The runtime's <c>TypeNames.Spellings</c> reads the names from reflection. Anything else that
/// reads type names from a setting — an analyzer option, from Roslyn symbols — has to accept the
/// same ones, or a name written once is a setting that silently does nothing in one place; so the
/// cases live here, outside any one test project, for the types declared in <see cref="Source"/>.
/// </remarks>
internal static class TypeSpellingCases
{
    /// <summary>The types the cases name, as C# source, so a compilation-based test can compile them too.</summary>
    public const string Source = """
        namespace Shop
        {
            public sealed class Order { }

            public static class Outer
            {
                public sealed class Worker<T> : Microsoft.Extensions.Hosting.IHostedService
                {
                    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
                    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
                }

                public sealed class Pair<TFirst, TSecond> : Microsoft.Extensions.Hosting.IHostedService
                {
                    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
                    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
                }
            }

            public static class Box<T>
            {
                public sealed class Item { }
            }
        }
        """;

    /// <summary>The ways to write <c>Shop.Outer.Worker&lt;Shop.Order&gt;</c> in a setting.</summary>
    public static readonly string[] WorkerOfOrder =
    [
        "Worker<Order>",
        "Shop.Outer+Worker<Order>",
        "Shop.Outer.Worker<Order>",
        "Shop.Outer.Worker<Shop.Order>",
        "Shop.Outer+Worker<Shop.Order>",
        "Worker<>",
        "Shop.Outer+Worker<>",
        "Shop.Outer.Worker<>",
        "Shop.Outer+Worker`1",
        "Shop.Outer.Worker`1",
    ];

    /// <summary>
    /// The ways to write <c>Shop.Outer.Pair&lt;Shop.Order, Shop.Order&gt;</c>: a comma inside the
    /// brackets is part of the name, not a list separator, and the space after it is optional.
    /// </summary>
    public static readonly string[] PairOfOrders =
    [
        "Pair<Order, Order>",
        "Pair<Order,Order>",
        "Shop.Outer.Pair<Order, Order>",
        "Shop.Outer.Pair<Shop.Order, Shop.Order>",
        "Pair<,>",
        "Shop.Outer+Pair<,>",
        "Shop.Outer.Pair`2",
    ];

    /// <summary>
    /// The ways to write <c>Shop.Box&lt;Shop.Order&gt;.Item</c>, a type nested in a generic one: the
    /// outer type's argument goes on the outer type's name, as C# writes it.
    /// </summary>
    public static readonly string[] ItemOfBox =
    [
        "Shop.Box<Order>.Item",
        "Shop.Box<Order>+Item",
        "Shop.Box<Shop.Order>.Item",
        "Shop.Box<>.Item",
        "Shop.Box`1+Item",
        "Shop.Box`1.Item",
    ];

    /// <summary>A list naming both workers, the comma between them a separator and the one inside a name not.</summary>
    public const string BothWorkers = "Shop.Outer.Pair<Order, Order>, Worker<>";
}
