// The types TypeSpellingCases names, compiled for the runtime tests. TypeSpellingCases.Source holds
// the same declarations as source; keep the two alike.
namespace Shop
{
    public sealed class Order;

    public static class Outer
    {
        public sealed class Worker<T> : Microsoft.Extensions.Hosting.IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class Pair<TFirst, TSecond> : Microsoft.Extensions.Hosting.IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }

    public static class Box<T>
    {
        public sealed class Item;
    }
}
