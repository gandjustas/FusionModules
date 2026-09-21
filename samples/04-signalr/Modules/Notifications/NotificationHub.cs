using Microsoft.AspNetCore.SignalR;

namespace NotificationsModule;

// Internal, like everything else in a module. SignalR resolves a hub by reflection, the way MVC
// resolves a controller, so MOD0001 exempts it — and unlike a controller there is no cascade to
// worry about, because a hub's methods are called by the framework and not by a caller who would
// need to see their types.
sealed class NotificationHub : Hub<INotificationClient>
{
    public const string Path = "/hubs/notifications";

    public Task Subscribe(string topic) =>
        Groups.AddToGroupAsync(Context.ConnectionId, topic);
}
