extern alias probe;
using FusionModules.Tool.Probe;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tool.Tests;

/// <summary>The probe's decisions that need no application to run in.</summary>
public class ProbeLogicTests
{
    [Test]
    public async Task AnAnnotationIsKnownByTheTypeItDerivesFrom()
    {
        // Aspire's WithEnvironment(name, "value") adds an EnvironmentAnnotation, an
        // EnvironmentCallbackAnnotation of its own type; read by its own name it was passed over, and
        // the AppHost's ASPNETCORE_HOSTINGSTARTUPASSEMBLIES with it.
        await Assert.That(probe::FusionModules.Tool.Probe.AppHostProbe.KnownAnnotation(typeof(AspireStandIns.EnvironmentAnnotation)))
            .IsEqualTo("EnvironmentCallbackAnnotation");
        await Assert.That(probe::FusionModules.Tool.Probe.AppHostProbe.KnownAnnotation(typeof(AspireStandIns.EnvironmentCallbackAnnotation)))
            .IsEqualTo("EnvironmentCallbackAnnotation");
        await Assert.That(probe::FusionModules.Tool.Probe.AppHostProbe.KnownAnnotation(typeof(AspireStandIns.ResourceIconAnnotation)))
            .IsEqualTo("ResourceIconAnnotation");
    }

    [Test]
    public async Task APooledDbContext_HasTheLifetimeOfItsRegistration()
    {
        // AddDbContextPool's order: the pool, then the context itself.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.EntityFrameworkCore.Internal.IDbContextPool<PooledContext>), new object());
        services.AddScoped<PooledContext>();
        services.AddSingleton(typeof(Microsoft.EntityFrameworkCore.Internal.IDbContextPool<FactoryOnlyContext>), new object());
        var result = new probe::FusionModules.Tool.Probe.ProbeOutput();

        probe::FusionModules.Tool.Probe.DbContexts.Describe([.. services], services.BuildServiceProvider(), result);

        var pooled = result.DbContexts!.Single(c => c.Type.EndsWith(nameof(PooledContext), StringComparison.Ordinal));
        await Assert.That(pooled.Pooled).IsTrue();
        await Assert.That(pooled.Lifetime).IsEqualTo("Scoped");
        await Assert.That(result.DbContexts!.Single(c => c.Type.EndsWith(nameof(FactoryOnlyContext), StringComparison.Ordinal)).Lifetime).IsEqualTo("Pooled");
    }

    [Test]
    public async Task AContextsModel_IsItsEntitiesInEfCoresWords_WithSeedDataAndFilters_AndWithoutBookkeeping()
    {
        var services = new ServiceCollection();
        services.AddScoped<ModelledContext>();
        services.AddScoped<BrokenContext>();
        var result = new probe::FusionModules.Tool.Probe.ProbeOutput();

        probe::FusionModules.Tool.Probe.DbContexts.Describe([.. services], services.BuildServiceProvider(), result);

        var model = result.DbContexts!.Single(c => c.Type.EndsWith(nameof(ModelledContext), StringComparison.Ordinal)).Model!;
        await Assert.That(model.Lines).IsEquivalentTo(["Annotations:", "  Relational:DefaultSchema: shop"]);
        await Assert.That(model.Entities.Select(entity => entity.Name)).IsEquivalentTo(["Shop.Customer", "Shop.Order"]);
        await Assert.That(model.Entities.Single(entity => entity.Name == "Shop.Order").Lines).IsEquivalentTo(
        [
            "EntityType: Order",
            "  Properties:",
            "    Id (int) Required PK",
            "  QueryFilter: o => (o.Length > value(FusionModules.Tool.Tests.ProbeLogicTests+ModelledContext+<>c__DisplayClass).minimum)",
            "  Seed: Id=1, Name=\"first\", Price=9.5",
            "  Seed: Id=2, Name=null, Price=10",
        ]);

        // A context that cannot be created has no model, and says why; nothing else of it is lost.
        var broken = result.DbContexts!.Single(c => c.Type.EndsWith(nameof(BrokenContext), StringComparison.Ordinal));
        await Assert.That(broken.Model).IsNull();
        await Assert.That(broken.ModelError).Contains("no tenant");
        await Assert.That(broken.Lifetime).IsEqualTo("Scoped");
    }

    [Test]
    public async Task ErrorDetail_ShowsTheThrowSite_AndWhereTheApplicationCalledIn()
    {
        // Passed in, not set on the static the probe sets once: the tests run in parallel.
        var application = new HashSet<string> { typeof(ProbeLogicTests).Assembly.GetName().Name! };

        var error = ProbeError.From("startup", Catch(ApplicationCode.Migrate), application);

        var lines = error.Detail!.Split('\n');
        await Assert.That(lines[0]).StartsWith("thrown at System.");
        await Assert.That(lines[1]).IsEqualTo("called from the application's code:");
        await Assert.That(lines[2]).StartsWith("  at FusionModules.Tool.Tests.ApplicationCode.Migrate");
        await Assert.That(error.Hint).StartsWith("The application's code after Build() threw (in FusionModules.Tool.Tests.ApplicationCode.Migrate)");
    }

    [Test]
    public async Task AConnectionFailureAfterBuild_SaysInfrastructureIsNotRunning()
    {
        var error = ProbeError.From("startup", Catch(() =>
            throw new InvalidOperationException($"Failed to connect to {ErrorHints.FakeAddress}", new System.Net.Sockets.SocketException(11001))));

        await Assert.That(error.Detail).Contains("caused by System.Net.Sockets.SocketException");
        await Assert.That(error.Hint).StartsWith("The application's code after Build() failed");
        await Assert.That(error.Hint).Contains("is not running or not reachable");
        await Assert.That(error.Hint).Contains("fake address");
        await Assert.That(error.Hint).Contains("--environment/--env");
    }

    [Test]
    public async Task AFileNotFound_InTheScratchWorkingDirectory_SaysSo()
    {
        var error = ProbeError.From("startup", Catch(() => throw new DirectoryNotFoundException($"Could not find a part of the path '{Path.Combine(Environment.CurrentDirectory, "Development")}'.")));

        await Assert.That(error.Hint).Contains("scratch working directory");
    }

    [Test]
    public async Task MissingConfiguration_PointsAtTheDeploymentOrEnv()
    {
        // eShop's WebApp in project mode: its AppHost is what sets IdentityUrl.
        var error = ProbeError.From("build", Catch(() => throw new InvalidOperationException("Configuration missing value for: IdentityUrl")));

        await Assert.That(error.Hint).Contains("on the solution or folder");
        await Assert.That(error.Hint).Contains("--env <Name>=<value>");
    }

    [Test]
    public async Task AMassTransitLicenseFailure_NamesTheVariable()
    {
        var error = ProbeError.From("build", Catch(() => throw new MassTransit.ConfigurationException("The license could not be loaded: not valid Base-64.")));

        await Assert.That(error.Hint).Contains("MT_LICENSE");
    }

    [Test]
    public async Task StaticAssets_CollapseIntoOneEntryAndACount()
    {
        var endpoints = Enumerable.Range(0, 2096)
            .Select(i => new ProbeEndpoint { Route = $"/css/site{i}.css", Kind = "other", Assembly = StaticAssets.Assembly })
            .Append(new ProbeEndpoint { Route = "/", Kind = "controller", Assembly = "WebUI" })
            .ToList();

        var count = StaticAssets.Collapse(endpoints);

        await Assert.That(count).IsEqualTo(2096);
        await Assert.That(endpoints.Count).IsEqualTo(2);
        await Assert.That(endpoints.Single(e => e.Kind == "static-assets").Route).IsNull();
    }

    [Test]
    public async Task OfSeveralRegistrations_TheLastWins_ButCollectionsAreLeftAlone()
    {
        var registrations = new List<ProbeService>
        {
            new() { ServiceType = "Duende.IdentityServer.Services.IProfileService", ImplementationType = "Identity.API.ProfileService" },
            new() { ServiceType = "Microsoft.Extensions.Options.IConfigureOptions<Identity.API.Settings>" },
            new() { ServiceType = "Duende.IdentityServer.Services.IProfileService", ImplementationType = "Duende.IdentityServer.AspNetIdentity.ProfileService<Identity.API.ApplicationUser>" },
            new() { ServiceType = "Microsoft.Extensions.Options.IConfigureOptions<Identity.API.Settings>" },
            new() { ServiceType = "Identity.API.IClock", Key = "a" },
            new() { ServiceType = "Identity.API.IClock", Key = "b" },
        };

        Overrides.Mark(registrations);

        await Assert.That(registrations.Select(r => r.Wins)).IsEquivalentTo(new bool?[] { false, null, true, null, null, null });
    }

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Nothing was thrown.");
    }

    private sealed class PooledContext : Microsoft.EntityFrameworkCore.DbContext;

    private sealed class BrokenContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public BrokenContext() => throw new InvalidOperationException("no tenant");
    }

    /// <summary>A context as EF Core's are reached: its internal services hand out the design-time model.</summary>
    private sealed class ModelledContext : Microsoft.EntityFrameworkCore.DbContext,
        Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>, IServiceProvider,
        Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel, Microsoft.EntityFrameworkCore.Metadata.IReadOnlyModel
    {
        public IServiceProvider Instance => this;

        public Microsoft.EntityFrameworkCore.Metadata.IReadOnlyModel Model => this;

        public object? GetService(Type serviceType) => serviceType == typeof(Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel) ? this : null;

        public string ToDebugString(Microsoft.EntityFrameworkCore.Infrastructure.MetadataDebugStringOptions options, int indent) =>
            options != Microsoft.EntityFrameworkCore.Infrastructure.MetadataDebugStringOptions.LongDefault
                ? "short"
                : """
                  Model:
                    EntityType: Order
                      Properties:
                        Id (int) Required PK
                    EntityType: Customer
                  Annotations:
                    BaseTypeDiscoveryConvention:DerivedTypes: System.Collections.Generic.Dictionary`2[System.Type,System.Collections.Generic.List`1[X]]
                    ProductVersion: 10.0.0
                    Relational:DefaultSchema: shop
                  """;

        public IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType> GetEntityTypes()
        {
            var minimum = 3;
            return
            [
                new Entity("Shop.Order", "EntityType: Order\n  Properties:\n    Id (int) Required PK\n  Annotations:\n    RelationshipDiscoveryConvention:NavigationCandidates: System.Collections.Immutable.ImmutableSortedDictionary`2[A,B]\n",
                    (System.Linq.Expressions.Expression<Func<string, bool>>)(o => o.Length > minimum),
                    new Dictionary<string, object?> { ["Name"] = null, ["Id"] = 2, ["Price"] = 10m },
                    new Dictionary<string, object?> { ["Price"] = 9.5m, ["Name"] = "first", ["Id"] = 1 }),
                new Entity("Shop.Customer", "EntityType: Customer\n", null),
            ];
        }

        private sealed class Entity(string name, string view, System.Linq.Expressions.LambdaExpression? filter, params IDictionary<string, object?>[] seed)
            : Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType
        {
            public string Name => name;

            public string ToDebugString(Microsoft.EntityFrameworkCore.Infrastructure.MetadataDebugStringOptions options, int indent) => view;

            public IEnumerable<IDictionary<string, object?>> GetSeedData(bool providerValues) => seed;

            public System.Linq.Expressions.LambdaExpression? GetQueryFilter() => filter;
        }
    }

    private sealed class FactoryOnlyContext : Microsoft.EntityFrameworkCore.DbContext;
}

internal static class ApplicationCode
{
    // Application code calling into a library that throws: the framework stands in for the library.
    public static void Migrate() => _ = int.Parse("not a number", System.Globalization.CultureInfo.InvariantCulture);
}

// Named as Aspire names them: the probe reads annotations by type name, never by type.
internal static class AspireStandIns
{
    internal class EnvironmentCallbackAnnotation;

    internal sealed class EnvironmentAnnotation : EnvironmentCallbackAnnotation;

    internal sealed class ResourceIconAnnotation;
}
