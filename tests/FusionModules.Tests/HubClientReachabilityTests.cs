using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tests;

/// <summary>
/// The runtime behaviour MOD0009 is built on, asserted rather than described.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer's own tests run against stub <c>Hub</c> types, because an analyzer test compiles
/// source and never starts anything. So nothing in this repository actually checked the claim the
/// rule makes — that SignalR's generated proxy cannot implement an interface it cannot see — and a
/// rule at severity Error should not rest on prose alone.
/// </para>
/// <para>
/// This assembly deliberately does not carry
/// <c>[assembly: InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]</c>. Adding
/// it would make the first test pass for the wrong reason, and there is no per-type way to grant
/// the access — which is itself part of what the rule is about.
/// </para>
/// </remarks>
public class HubClientReachabilityTests
{
    [Test]
    public async Task InternalClientInterface_FailsWhenTheProxyIsBuilt()
    {
        // Clean build, no other rule, green tests — and this, the first time a client connects.
        var failure = Record(() => Resolve<InternalClientHub, IInternalPaymentClient>());

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure).IsTypeOf<TypeLoadException>();
        await Assert.That(failure!.Message).Contains(nameof(IInternalPaymentClient));
    }

    [Test]
    public async Task PublicClientInterface_Works()
    {
        // The other half: the failure above is about visibility and nothing else.
        var failure = Record(() => Resolve<PublicClientHub, IPublicPaymentClient>());

        await Assert.That(failure).IsNull();
    }

    private static void Resolve<THub, TClient>()
        where THub : Hub<TClient>
        where TClient : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();

        using var provider = services.BuildServiceProvider();

        // The proxy type is built lazily, so resolving the context is not on its own enough.
        _ = provider.GetRequiredService<IHubContext<THub, TClient>>().Clients.All;
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            // TypedClientBuilder reflects, so the real cause arrives wrapped.
            while (e is TargetInvocationException { InnerException: { } inner })
            {
                e = inner;
            }

            return e;
        }
    }
}

internal interface IInternalPaymentClient
{
    Task Paid(string reference);
}

public interface IPublicPaymentClient
{
    Task Paid(string reference);
}

internal sealed class InternalClientHub : Hub<IInternalPaymentClient>;

internal sealed class PublicClientHub : Hub<IPublicPaymentClient>;
