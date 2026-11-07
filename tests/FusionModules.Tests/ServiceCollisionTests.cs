using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Tests;

/// <summary>
/// Two modules, one container: what happens when both register the same service.
/// </summary>
/// <remarks>
/// Modelled on what eShop hit. The Catalog and Ordering outboxes each registered their own
/// <c>IIntegrationEventLogService</c>. Merged into one host, the last registration won, and one
/// module wrote its events through the other's DbContext.
/// </remarks>
public class ServiceCollisionTests
{
    [Test]
    public async Task DifferentImplementationsFromTwoModules_FailStartup()
    {
        var failure = await StartAsync([new CatalogModule(), new OrderingModule()]);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains(typeof(IEventLog).FullName!);
        await Assert.That(failure.Message).Contains("CatalogEventLog");
        await Assert.That(failure.Message).Contains("OrderingEventLog");
        await Assert.That(failure.Message).Contains("(CatalogModule)");
        await Assert.That(failure.Message).Contains("(OrderingModule)");
    }

    [Test]
    public async Task TwoModulesOwnClassesOfOneName_AreTwoImplementations()
    {
        // dotnext-2026's load test: every transport module calls its implementation Calculator, and
        // compared by name, the Local and gRPC transports started together.
        var failure = await StartAsync([new LocalTransport(), new GrpcTransport()]);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains(typeof(ICalculator).FullName!);
        // The two read the same by short name, so the message names them in full.
        await Assert.That(failure.Message).Contains($"{typeof(LocalTransport).FullName}+Calculator");
        await Assert.That(failure.Message).Contains($"{typeof(GrpcTransport).FullName}+Calculator");
    }

    [Test]
    public async Task ATryAddThatAddsNothing_IsNotSeen()
    {
        // First-wins hands a module the wrong implementation just as last-wins does — which is why
        // "use TryAdd" was the wrong advice. A TryAdd that adds nothing adds no descriptor, so the
        // check sees one registration; the module that lost has no way to notice. The check covers
        // the case where both do add — and the advice changes.
        var failure = await StartAsync([new CatalogModule(), new OrderingModule(tryAdd: true)]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task ARegistrationTheTestReplaced_NoLongerCollides()
    {
        // ConfigureTestServices takes the modules' event logs out and puts its own in: the container
        // the host starts with has no collision, and neither does the check.
        var result = await ModuleHost.StartAsync(
            [new CatalogModule(), new OrderingModule()],
            afterModules: services =>
            {
                services.RemoveAll<IEventLog>();
                services.AddSingleton<IEventLog, CatalogEventLog>();
            });

        await Assert.That(result.Failure).IsNull();
    }

    [Test]
    public async Task TheSameImplementationFromTwoModules_IsFine()
    {
        // Redundant rather than wrong: whichever registration wins, each module gets what it asked for.
        var failure = await StartAsync([new CatalogModule(), new CatalogTwinModule()]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task KeyedRegistrations_WithDifferentKeys_AreFine()
    {
        var failure = await StartAsync([new KeyedModule("catalog", typeof(CatalogEventLog)), new KeyedModule("ordering", typeof(OrderingEventLog))]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task KeyedRegistrations_WithTheSameKey_FailStartup()
    {
        var failure = await StartAsync([new KeyedModule("shared", typeof(CatalogEventLog)), new OtherKeyedModule("shared", typeof(OrderingEventLog))]);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("[key: shared]");
    }

    [Test]
    public async Task FrameworkContracts_AreFine()
    {
        // Hosted services, options configuration and the like are meant to be registered many
        // times and read as a sequence.
        var failure = await StartAsync([new WorkerModule<FirstWorker>(), new OtherWorkerModule<SecondWorker>()]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task EfCoreNonGenericDbContextOptions_FromTwoModules_IsFine()
    {
        // Two modules with a DbContext each: both register the non-generic options, ambiguous by
        // design, and each context is built from its own generic options instead.
        var failure = await StartAsync([new EfModule("catalog"), new OtherEfModule("webhooks")]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AnAllowListedType_IsFine()
    {
        var failure = await StartAsync(
            [new CatalogModule(), new OrderingModule()],
            ("FusionModules:AllowedServiceCollisions:0", typeof(IEventLog).FullName!));

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AContractMarkedForSeveralImplementations_IsFine()
    {
        // The contract says it is read as a sequence; no host has to repeat it in configuration.
        var failure = await StartAsync([new ListenerModule<FirstListener>(), new OtherListenerModule<SecondListener>()]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AGenericContractMarkedForSeveralImplementations_IsFine()
    {
        // The attribute is declared on the definition, and covers every construction of it.
        var failure = await StartAsync([new HandlerModule<FirstHandler>(), new OtherHandlerModule<SecondHandler>()]);

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task TheMessage_FirstAsksWhetherTheModulesAreAlternatives()
    {
        // dotnext-2026's Local and RemoteGrpc transports are meant for different deployments: the fix
        // is the module list, and keying the registration would only have hidden that.
        var failure = await StartAsync([new LocalTransport(), new GrpcTransport()]);

        var advice = failure!.Message.Split(Environment.NewLine).SkipWhile(line => line.StartsWith(' ') || line.StartsWith("Two modules", StringComparison.Ordinal)).ToList();

        await Assert.That(advice).IsEquivalentTo(
            [
                "If these modules are alternatives that no topology should load together, remove one of them from HOSTINGSTARTUPASSEMBLIES.",
                "Otherwise register a keyed service, or a service type of each module's own. TryAdd does not help: first-wins hands a module the wrong implementation just as last-wins does.",
                "If the type is meant to be consumed as IEnumerable<T>, put an attribute named MultipleImplementationsAttribute on it (declared anywhere; matched by name), " +
                "or list it under FusionModules:AllowedServiceCollisions when the type is not yours.",
            ],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AContractTheHostsBuildListsAsAlternatives_StillFailsStartup_AndTheMessageSaysSo()
    {
        // What the generated ValidateModules writes from the host's fusion_modules_allowed_service_collisions:
        // the build knows these modules are alternatives, so the message does not have to ask.
        var failure = await StartAsync(
            [new CatalogModule(), new OrderingModule()],
            ("FusionModules:AlternativeServices", $"Some.Other.IContract;{nameof(IEventLog)}"));

        await Assert.That(failure).IsNotNull();
        var lines = failure!.Message.Split(Environment.NewLine);
        await Assert.That(lines[^1]).IsEqualTo(
            $"{typeof(IEventLog).FullName} is listed in the host's build as implemented by alternative modules (fusion_modules_allowed_service_collisions): " +
            "load only one of FusionModules.Tests (CatalogModule), FusionModules.Tests (OrderingModule) in a topology — " +
            "remove the others from HOSTINGSTARTUPASSEMBLIES.");
        await Assert.That(failure.Message).DoesNotContain("If these modules are alternatives");
    }

    [Test]
    public async Task TheAlternatives_AreNamedInOneOrder_WhateverOrderTheyRegistered()
    {
        // HOSTINGSTARTUPASSEMBLIES may name them either way round; the advice reads the same.
        var setting = ("FusionModules:AlternativeServices", nameof(IEventLog));
        var forward = await StartAsync([new CatalogModule(), new OrderingModule()], setting);
        var backward = await StartAsync([new OrderingModule(), new CatalogModule()], setting);

        await Assert.That(forward!.Message.Split(Environment.NewLine)[^1]).IsEqualTo(backward!.Message.Split(Environment.NewLine)[^1]);
    }

    [Test]
    public async Task AContractTheBuildDoesNotList_KeepsTheGeneralAdvice()
    {
        var failure = await StartAsync(
            [new CatalogModule(), new OrderingModule(), new LocalTransport(), new GrpcTransport()],
            ("FusionModules:AlternativeServices", nameof(ICalculator)));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains($"{typeof(ICalculator).FullName} is listed in the host's build as implemented by alternative modules");
        await Assert.That(failure.Message).DoesNotContain($"{typeof(IEventLog).FullName} is listed");
        await Assert.That(failure.Message).Contains("If these modules are alternatives that no topology should load together");
    }

    [Test]
    public async Task AnAllowListedType_ByItsShortName_IsFine()
    {
        // Any of the spellings in TypeSpellingCases: a name that silently does nothing because it
        // was written one plausible way rather than another is the failure to avoid.
        var failure = await StartAsync(
            [new CatalogModule(), new OrderingModule()],
            ("FusionModules:AllowedServiceCollisions", nameof(IEventLog)));

        await Assert.That(failure).IsNull();
    }

    [Test]
    [Arguments("Warning")]
    [Arguments("Ignore")]
    public async Task NoModeSetting_TurnsTheCheckOff(string mode)
    {
        // There is no switch that makes a collision a warning or nothing: a collision the host has
        // to live with is named in the allow-list, one type at a time. The obvious spelling of such
        // a switch is not read.
        var failure = await StartAsync([new CatalogModule(), new OrderingModule()], ("FusionModules:ServiceCollisions", mode));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).DoesNotContain("FusionModules:ServiceCollisions");
    }

    [Test]
    public async Task WithTheRuntimeChecksOff_ACollisionDoesNotStopStartup()
    {
        // What ValidateModules(runtimeTopologyChecks: false) writes; the build-time MOD0014 still
        // reports it.
        var failure = await StartAsync([new CatalogModule(), new OrderingModule()], ("FusionModules:RuntimeTopologyChecks", "false"));

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task TheHostsOwnRegistration_IsNotAModuleCollision()
    {
        // The host may override a module's default deliberately; that is not two modules disagreeing.
        var failure = (await ModuleHost.StartAsync([new CatalogModule()], configureHost: services => services.AddSingleton<IEventLog, OrderingEventLog>())).Failure;

        await Assert.That(failure).IsNull();
    }

    private static async Task<Exception?> StartAsync(ModuleBase[] modules, params (string Key, string Value)[] settings) =>
        (await ModuleHost.StartAsync(modules, settings: settings)).Failure;

    internal interface IEventLog;

    [MultipleImplementations]
    internal interface IListener;

    internal sealed class FirstListener : IListener;

    internal sealed class SecondListener : IListener;

    private class ListenerModule<TListener> : ModuleBase where TListener : class, IListener
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped<IListener, TListener>();
    }

    private sealed class OtherListenerModule<TListener> : ListenerModule<TListener> where TListener : class, IListener;

    [MultipleImplementations]
    internal interface IHandler<T>;

    internal sealed class FirstHandler : IHandler<int>;

    internal sealed class SecondHandler : IHandler<int>;

    private class HandlerModule<THandler> : ModuleBase where THandler : class, IHandler<int>
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped<IHandler<int>, THandler>();
    }

    private sealed class OtherHandlerModule<THandler> : HandlerModule<THandler> where THandler : class, IHandler<int>;

    internal interface ICalculator;

    private sealed class LocalTransport : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddTransient<ICalculator, Calculator>();

        internal sealed class Calculator : ICalculator;
    }

    private sealed class GrpcTransport : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddTransient<ICalculator, Calculator>();

        internal sealed class Calculator : ICalculator;
    }

    internal sealed class CatalogEventLog : IEventLog;

    internal sealed class OrderingEventLog : IEventLog;

    private sealed class CatalogModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped<IEventLog, CatalogEventLog>();
    }

    private sealed class CatalogTwinModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped<IEventLog, CatalogEventLog>();
    }

    private sealed class OrderingModule(bool tryAdd = false) : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder)
        {
            if (tryAdd)
            {
                builder.Services.TryAddScoped<IEventLog, OrderingEventLog>();
            }
            else
            {
                builder.Services.AddScoped<IEventLog, OrderingEventLog>();
            }
        }
    }

    private class KeyedModule(string key, Type implementation) : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddKeyedScoped(typeof(IEventLog), key, implementation);
    }

    private sealed class OtherKeyedModule(string key, Type implementation) : KeyedModule(key, implementation);

    private class EfModule(string context) : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped(_ => new Microsoft.EntityFrameworkCore.DbContextOptions(context));
    }

    private sealed class OtherEfModule(string context) : EfModule(context);

    internal sealed class FirstWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    internal sealed class SecondWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private class WorkerModule<TWorker> : ModuleBase where TWorker : class, IHostedService
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddHostedService<TWorker>();
    }

    private sealed class OtherWorkerModule<TWorker> : WorkerModule<TWorker> where TWorker : class, IHostedService;
}

/// <summary>The attribute as an application declares it: its own, internal, matched by name.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
internal sealed class MultipleImplementationsAttribute : Attribute;
