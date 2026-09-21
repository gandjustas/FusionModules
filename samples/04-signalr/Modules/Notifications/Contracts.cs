// What the server pushes to a connected client, and the payload it pushes.
//
// SignalR does not implement INotificationClient in this assembly. It generates an implementation
// at runtime with TypedClientBuilder, into a dynamic assembly of its own — and a type there cannot
// implement an internal interface declared here. Without the attribute below, this compiles, no
// rule fires, every test that does not start a hub passes, and the process throws
// TypeLoadException the first time it resolves IHubContext. MOD0009 is what turns that into a
// build error; this attribute is what answers it.
//
// The attribute is worth preferring over making the interface public, because public spreads: a
// public interface member cannot take a less accessible type, so Notification would follow, and
// then whatever Notification names. Here the whole closure stays internal.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]

namespace NotificationsModule;

interface INotificationClient
{
    Task Notify(Notification notification);
}

sealed record Notification(string Subject, string Body);
