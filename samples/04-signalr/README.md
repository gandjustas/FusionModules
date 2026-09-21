# 04 — SignalR

A realtime module, and the one failure in this whole approach that a build, a test suite and a
route inventory can all miss at once.

```bash
cd Host
dotnet run --launch-profile NoModules       # /
dotnet run --launch-profile Notifications   # / and /hubs/notifications and POST /notify/{topic}
```

## The thing worth reading

A `Hub<TClient>` does not call your client interface. SignalR generates an implementation of it at
runtime, into a dynamic assembly named `Microsoft.AspNetCore.SignalR.TypedClientBuilder` — and a
type there cannot implement an `internal` interface declared in yours.

So MOD0001, which wants every type in a module internal, points straight at a
`TypeLoadException`. Not at build time: the project compiles, no rule fires, and every test that
does not actually start a hub passes. The process throws the first time it resolves
`IHubContext<NotificationHub, INotificationClient>`, which is the first time a real client
connects.

[`Modules/Notifications/Contracts.cs`](Modules/Notifications/Contracts.cs) answers it in one line:

```csharp
[assembly: InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]
```

The generated assembly is built with `AssemblyBuilderAccess.Run` and is therefore unsigned, so its
name is all the attribute needs. Comment the line out and the build fails with
[MOD0009](../../docs/rules/MOD0009.md) instead of the process failing later — which is the whole
point of the rule.

Making the interface `public` is the other way, and it spreads: a public interface member cannot
take a less accessible type, so `Notification` would follow it out, and then whatever `Notification`
names. Here the entire closure stays internal.

## What else to look at

**[`Modules/Notifications/NotificationHub.cs`](Modules/Notifications/NotificationHub.cs)** — the
hub is internal. SignalR resolves it by reflection the way MVC resolves a controller, so MOD0001
exempts it; unlike a controller there is no cascade, because a hub's methods are called by the
framework rather than by anyone who would need to name their types.

**[`Modules/Notifications/Module.cs`](Modules/Notifications/Module.cs)** — `MapHub` lives in the
module's `Configure`, because no application part discovers a hub. And a note on `AddSignalR`
worth taking seriously in a merged process: it configures `HubOptions` for the whole application,
not for this hub, so a second SignalR module silently overwrites whatever the first one set, and
which one wins depends on `HOSTINGSTARTUPASSEMBLIES` order.

**[`Tests/NotificationHubTests.cs`](Tests/NotificationHubTests.cs)** — a real `HubConnection`
against the test server, subscribing to a group and receiving a push. It is deliberately not a
mock: mocking the hub context is exactly the test that would have stayed green through the bug this
sample is about. The test declares its own payload record rather than reaching for the module's,
which is the design working — a wire contract is a wire contract.
