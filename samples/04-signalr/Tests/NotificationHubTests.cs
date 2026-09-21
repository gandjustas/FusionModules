using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace Sample.SignalR.Tests;

/// <summary>
/// A real client, connected to a real hub, receiving a push through a typed client interface that
/// nothing outside its module can name.
/// </summary>
/// <remarks>
/// The last part is what this sample exists for. SignalR generates the implementation of
/// <c>INotificationClient</c> into a dynamic assembly, so an internal interface is unreachable to
/// it unless the module says otherwise — and the failure is a <c>TypeLoadException</c> at the first
/// resolve, invisible to the build, to every MOD rule but MOD0009, and to any test that does not
/// actually start a hub. Which is why this test does.
/// </remarks>
public class NotificationHubTests
{
    private const string NotificationsModule = "NotificationsModule";

    [Test]
    public async Task NoModules_HasNoHub(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/hubs/notifications", cancellationToken)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.PostAsync("/notify/orders", null, cancellationToken)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ASubscribedClientReceivesThePush(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(NotificationsModule);

        await using var connection = Connect(factory);
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        // "Notify" is the method on INotificationClient. The proxy that implements it is built the
        // moment the hub context is first resolved, which is the moment the whole thing either
        // works or throws.
        connection.On<NotificationDto>("Notify", notification => received.TrySetResult(notification));

        await connection.StartAsync(cancellationToken);
        await connection.InvokeAsync("Subscribe", "orders", cancellationToken);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/notify/orders",
            new NotificationDto("Order shipped", "ORD-1"),
            cancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        await Assert.That(notification.Subject).IsEqualTo("Order shipped");
        await Assert.That(notification.Body).IsEqualTo("ORD-1");
    }

    [Test]
    public async Task AClientOutsideTheGroupIsNotPushedTo(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(NotificationsModule);

        await using var connection = Connect(factory);
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<NotificationDto>("Notify", notification => received.TrySetResult(notification));

        await connection.StartAsync(cancellationToken);
        await connection.InvokeAsync("Subscribe", "orders", cancellationToken);

        await factory.CreateClient().PostAsJsonAsync(
            "/notify/invoices",
            new NotificationDto("Invoice paid", "INV-1"),
            cancellationToken);

        var arrived = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));

        await Assert.That(arrived == received.Task).IsFalse();
    }

    /// <summary>
    /// Long polling because the transport has to travel through the in-memory test server's own
    /// handler; WebSockets would need a socket the test server is not offering.
    /// </summary>
    private static HubConnection Connect(ModularWebApplicationFactory factory) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/notifications"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    /// <summary>
    /// The test's own shape for the payload. It cannot name the module's <c>Notification</c>, and
    /// that is the design working rather than an inconvenience — a wire contract is a wire
    /// contract, and a test that reaches for the internal type would be testing the assembly
    /// instead of the protocol.
    /// </summary>
    private sealed record NotificationDto(string Subject, string Body);
}
