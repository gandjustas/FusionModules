using Microsoft.AspNetCore.SignalR;

namespace NotificationsModule;

// Internal, like everything else in a module, as MOD0001 requires. MapHub names the type, so nothing
// has to discover it. The constructor and the hub methods stay public (the activator and the
// dispatcher look for public members only), and as members of an internal class they may take the
// module's internal services and models — a public hub would drag those public with it.
sealed class NotificationHub : Hub<INotificationClient>
{
    public const string Path = "/hubs/notifications";

    public Task Subscribe(string topic) =>
        Groups.AddToGroupAsync(Context.ConnectionId, topic);
}
