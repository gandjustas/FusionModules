using FusionModules.Tool.Probe;

namespace FusionModules.Tool.Tests;

/// <summary>Collisions across hosts, and the Aspire model turned into services and fake configuration.</summary>
public class AnalysisTests
{
    [Test]
    [Arguments("/api/items/{id}", "/api/items/{}")]
    [Arguments("api/Items/{itemId}/", "/api/items/{}")]
    [Arguments("/api/items/{id:int}", "/api/items/{int}")]
    [Arguments("/api/items/{id:int:min(1)}", "/api/items/{int,min(1)}")]
    [Arguments("/orders/{id?}", "/orders/{?}")]
    [Arguments("/orders/{page=1}", "/orders/{?}")]
    [Arguments("/files/{**path}", "/files/{*}")]
    [Arguments("/", "/")]
    public async Task Routes_AreComparedAsTheMatcherComparesThem(string route, string expected) =>
        await Assert.That(Names.Route(route)).IsEqualTo(expected);

    [Test]
    public async Task TheSameRouteInTwoServices_WithOverlappingMethods_Collides()
    {
        var hosts = new[]
        {
            Host("catalog", endpoints: [Endpoint("/api/items/{id}", "GET"), Endpoint("/health", null, "HealthCheckMiddleware.InvokeAsync")]),
            Host("ordering", endpoints: [Endpoint("/api/items/{itemId}", "GET", "Ordering.Items.Get"), Endpoint("/api/items/{id}", "DELETE"), Endpoint("/health", null, "HealthCheckMiddleware.InvokeAsync")]),
            Host("basket", endpoints: [Endpoint("/api/items/{id}", "POST"), Endpoint("/api/items/{id:int}", "GET")]),
        };

        var collisions = Collisions.Find(hosts).Routes;

        var items = collisions.Single(c => c.Route == "/api/items/{}");
        await Assert.That(items.Sites.Select(s => s.Service)).IsEquivalentTo(["catalog", "ordering"]);
        await Assert.That(items.SharedHandler).IsFalse();
        // A constraint makes it another route to the matcher: basket's {id:int} is not catalog's {id}.
        await Assert.That(collisions.Any(c => c.Route == "/api/items/{int}")).IsFalse();
        await Assert.That(collisions.Single(c => c.Route == "/health").SharedHandler).IsTrue();
    }

    [Test]
    public async Task AConfigurationKey_WithTwoValues_Collides_PerFileAndInTheEnvironment()
    {
        var catalog = Host("catalog", configuration: new() { ["appsettings.json"] = new() { ["EventBus:Name"] = "Catalog", ["Same"] = "1" } },
            environment: new() { ["Identity__Url"] = "http://a", ["ConnectionStrings__db"] = "Host=localhost" }, faked: ["ConnectionStrings__db"]);
        var ordering = Host("ordering", configuration: new() { ["appsettings.json"] = new() { ["EventBus:Name"] = "Ordering", ["Same"] = "1" } },
            environment: new() { ["Identity__Url"] = "http://b", ["ConnectionStrings__db"] = "Host=elsewhere" }, faked: ["ConnectionStrings__db"]);

        var collisions = Collisions.Find([catalog, ordering]).ConfigurationKeys;

        await Assert.That(collisions.Select(c => (c.Source, c.Key))).IsEquivalentTo([("appsettings.json", "EventBus:Name"), ("environment", "Identity:Url")]);
    }

    [Test]
    public async Task AnApplicationServiceType_WithTwoImplementations_Collides()
    {
        var catalog = Host("catalog", services: [Service("Shared.IEventLog", "Shared", "Shared.EventLog<Catalog.Db>"), Service("Microsoft.Extensions.Hosting.IHostedService", "Microsoft.Extensions.Hosting.Abstractions", "Catalog.Worker")]);
        var ordering = Host("ordering", services: [Service("Shared.IEventLog", "Shared", "Shared.EventLog<Ordering.Db>"), Service("Microsoft.Extensions.Hosting.IHostedService", "Microsoft.Extensions.Hosting.Abstractions", "Ordering.Worker")]);

        var collision = Collisions.Find([catalog, ordering]).Services.Single();

        await Assert.That(collision.ServiceType).IsEqualTo("Shared.IEventLog");
        await Assert.That(collision.Sites.SelectMany(s => s.Implementations)).IsEquivalentTo(["Shared.EventLog<Catalog.Db>", "Shared.EventLog<Ordering.Db>"]);
    }

    [Test]
    public async Task AspireExpressions_BecomeFakesThatParse()
    {
        var model = new ProbeAspireModel
        {
            Resources =
            [
                new() { Name = "catalogdb", Type = "PostgresDatabaseResource", Parent = "postgres" },
                new() { Name = "redis", Type = "RedisResource" },
                new() { Name = "basket-api", Type = "ProjectResource", Endpoints = [new() { Name = "http", Scheme = "http", Port = 5221 }] },
                new()
                {
                    Name = "webapp", Type = "ProjectResource", ProjectPath = "webapp.csproj",
                    Environment = new()
                    {
                        ["ConnectionStrings__catalogdb"] = "{catalogdb.connectionString}",
                        ["ConnectionStrings__redis"] = "{redis.connectionString}",
                        ["services__basket-api__http__0"] = "{basket-api.bindings.http.url}",
                        ["Password"] = "{password.value}",
                        ["Plain"] = "value",
                    },
                },
            ],
        };

        var (environment, faked) = AspireModel.ProbeEnvironment(model.Resources[3], model);

        await Assert.That(environment["ConnectionStrings__catalogdb"]).StartsWith($"Host={FakeConfiguration.Host};Port={FakeConfiguration.Port};Database=catalogdb");
        await Assert.That(environment["ConnectionStrings__redis"]).StartsWith($"{FakeConfiguration.Address},");
        await Assert.That(environment["services__basket-api__http__0"]).IsEqualTo($"http://{FakeConfiguration.Address}");
        await Assert.That(environment["Password"]).IsEqualTo(FakeConfiguration.Secret);
        await Assert.That(environment["Plain"]).IsEqualTo("value");
        await Assert.That(faked).DoesNotContain("Plain");

        var services = AspireModel.Services(model, p => p);
        await Assert.That(services.Single(s => s.Name == "webapp").References!.Select(r => r.Name)).IsEquivalentTo(["catalogdb", "redis", "basket-api"]);
        await Assert.That(services.Single(s => s.Name == "catalogdb").Kind).IsEqualTo("other");
    }

    [Test]
    public async Task AMissingConnectionString_IsReadOffAspiresMessage()
    {
        var errors = new[]
        {
            ProbeError.Of("build", "ConnectionString is missing. It should be provided in 'ConnectionStrings:orders' or under the 'ConnectionString' key in 'Aspire:Npgsql:EntityFrameworkCore:PostgreSQL' configuration section."),
        };

        var missing = FakeConfiguration.MissingConnectionString(errors);

        await Assert.That(missing).IsEqualTo(("orders", "postgres"));
    }

    [Test]
    public async Task AnAspireManifest_IsTheSameModel()
    {
        const string manifest = """
            {
              "resources": {
                "cache": { "type": "container.v0", "connectionString": "{cache.bindings.tcp.host}:{cache.bindings.tcp.port}", "image": "docker.io/library/redis:7.4",
                           "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 6379 } } },
                "api": { "type": "project.v0", "path": "../Api/Api.csproj",
                         "env": { "ConnectionStrings__cache": "{cache.connectionString}" },
                         "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "external": true } } }
              }
            }
            """;

        var model = AspireModel.ParseManifest(manifest, Path.Combine(Path.GetTempPath(), "AppHost"));
        var services = AspireModel.Services(model, p => p);

        var api = services.Single(s => s.Name == "api");
        await Assert.That(api.Kind).IsEqualTo("project");
        await Assert.That(api.Project).IsEqualTo(Path.Combine(Path.GetTempPath(), "Api", "Api.csproj"));
        await Assert.That(api.Ports!.Single().External).IsTrue();
        await Assert.That(api.References!.Single().Name).IsEqualTo("cache");
        await Assert.That(services.Single(s => s.Name == "cache").Kind).IsEqualTo("container");
    }

    [Test]
    public async Task SeveralHostsStoppedOverTheSchema_InParallel_AreToldToRunOneAtATime()
    {
        static HostInfo Stopped(string service, string message) => new()
        {
            Service = service,
            Runtime = new ProbeOutput { Status = "built", Errors = [new ProbeError { Stage = "startup", Type = "Npgsql.PostgresException", Message = message }] },
        };

        HostInfo[] hosts =
        [
            Stopped("dice", "42P01: relation \"partner_game_settings\" does not exist"),
            Stopped("hilo", "42P07: relation \"partner_game_settings\" already exists"),
            Stopped("auth", "Failed to connect to 127.0.0.1:1"),
            new() { Service = "admin", Runtime = new ProbeOutput { Status = "started" } },
        ];

        var race = Inventory.MigrationRace(hosts, parallelism: 4);

        await Assert.That(race).Contains("2 hosts (dice, hilo)").And.Contains("--parallel 1");
        await Assert.That(Inventory.MigrationRace(hosts, parallelism: 1)).IsNull();
        await Assert.That(Inventory.MigrationRace(hosts.Skip(1), parallelism: 4)).IsNull();
    }

    [Test]
    public async Task TheProgressLine_OfAHostThatDidNotStart_SaysWhy()
    {
        var skipped = new ProbeOutput { Status = "skipped", Errors = [ProbeError.Of("probe", "It does not use Microsoft.Extensions.Hosting.")] };
        var built = new ProbeOutput
        {
            Status = "built",
            Services = [],
            Endpoints = [new ProbeEndpoint { Route = "/now" }],
            EndpointsReadFrom = "application",
            Errors = [new ProbeError { Stage = "startup", Type = "Npgsql.NpgsqlException", Message = "Failed to connect\nmore", Hint = "Start PostgreSQL." }],
        };

        await Assert.That(Inventory.ProgressLine("tool", skipped)).IsEqualTo("  tool: skipped (0 ms)\n    It does not use Microsoft.Extensions.Hosting.");
        await Assert.That(Inventory.ProgressLine("api", built)).IsEqualTo(
            "  api: built, 0 application services, 1 endpoints (read from application) (0 ms)\n    Npgsql.NpgsqlException: Failed to connect\n    Start PostgreSQL.");
        await Assert.That(Inventory.Reason(built)).IsEqualTo("Npgsql.NpgsqlException: Failed to connect\nmore\nStart PostgreSQL.");
    }

    private static HostInfo Host(
        string service,
        List<ProbeEndpoint>? endpoints = null,
        SortedDictionary<string, SortedDictionary<string, string?>>? configuration = null,
        SortedDictionary<string, string>? environment = null,
        List<string>? faked = null,
        List<ProbeService>? services = null) => new()
        {
            Service = service,
            Configuration = configuration,
            Environment = environment,
            FakedEnvironment = faked,
            Runtime = new ProbeOutput
            {
                Status = "started",
                Endpoints = endpoints,
                Services = services,
                ApplicationAssemblies = ["Shared", "Catalog", "Ordering"],
            },
        };

    private static ProbeEndpoint Endpoint(string route, string? method, string? handler = null) => new()
    {
        Route = route,
        Methods = method is null ? null : [method],
        Handler = handler ?? $"Handler{route.GetHashCode()}",
    };

    private static ProbeService Service(string serviceType, string serviceAssembly, string implementation) => new()
    {
        ServiceType = serviceType,
        ServiceAssembly = serviceAssembly,
        ImplementationType = implementation,
        Implementation = "type",
        Lifetime = "Singleton",
    };
}
