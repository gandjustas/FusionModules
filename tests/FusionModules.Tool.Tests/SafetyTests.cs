using FusionModules.Tool.Probe;

namespace FusionModules.Tool.Tests;

/// <summary>
/// What the tool invents must never reach a real service on this machine, and what it reads must
/// not leak secrets into the document.
/// </summary>
public class SafetyTests
{
    public static IEnumerable<string> Kinds() => FakeConfiguration.Kinds;

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task EveryFakeConnectionString_PointsAtTheDeadLoopbackPort_AndNoName(string kind)
    {
        var connectionString = FakeConfiguration.ConnectionString(kind, "orders");
        var port = FakeConfiguration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 127.0.0.1 and the port, never a name: a resolver may answer any name, .invalid included.
        await Assert.That(connectionString).Contains($"{FakeConfiguration.Host}:{port}").Or.Contains($"{FakeConfiguration.Host},{port}").Or.Contains($"{FakeConfiguration.Host};Port={port}");
        await Assert.That(connectionString).DoesNotContain("localhost");
        await Assert.That(connectionString).DoesNotContain(".invalid");
        await Assert.That(connectionString).DoesNotContain("UseDevelopmentStorage");
        foreach (var defaultPort in new[] { "5432", "1433", "3306", "6379", "5672", "27017", "9092", "10000", "2113" })
        {
            await Assert.That(connectionString).DoesNotContain(":" + defaultPort).And.DoesNotContain("=" + defaultPort).And.DoesNotContain("," + defaultPort);
        }
    }

    [Test]
    public async Task TheFakePort_IsOneThatRefusesConnections()
    {
        // A port that is listened on is passed over.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var taken = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            var port = FakeConfiguration.FindDeadPort([taken, .. FakeConfiguration.PortCandidates]);

            await Assert.That(port).IsNotEqualTo(taken);
            using var client = new System.Net.Sockets.TcpClient();
            await Assert.That(async () => await client.ConnectAsync(System.Net.IPAddress.Loopback, port)).Throws<System.Net.Sockets.SocketException>();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task AResourcesOwnConnectionStringExpression_IsFaked_NotGuessedFromItsType()
    {
        var model = new ProbeAspireModel
        {
            Resources =
            [
                // A type the tool has never heard of, whose client insists on its own scheme.
                new() { Name = "kurrentdb", Type = "KurrentDBResource", ConnectionString = "kurrentdb://{kurrentdb.bindings.http.host}:{kurrentdb.bindings.http.port}?tls=false" },
                new() { Name = "postgres", Type = "PostgresServerResource", ConnectionString = "Host={postgres.bindings.tcp.host};Port={postgres.bindings.tcp.port};Username=postgres;Password={postgres-password.value}" },
                new() { Name = "catalogdb", Type = "PostgresDatabaseResource", Parent = "postgres", ConnectionString = "{postgres.connectionString};Database=catalogdb" },
                // Bicep outputs: nothing to fake them from, so the guess by type.
                new() { Name = "blobs", Type = "AzureBlobStorageResource", ConnectionString = "{storage.outputs.blobEndpoint}" },
                new()
                {
                    Name = "api", Type = "ProjectResource",
                    Environment = new()
                    {
                        ["ConnectionStrings__kurrentdb"] = "{kurrentdb.connectionString}",
                        ["ConnectionStrings__catalogdb"] = "{catalogdb.connectionString}",
                        ["ConnectionStrings__blobs"] = "{blobs.connectionString}",
                    },
                },
            ],
        };

        var (environment, _) = AspireModel.ProbeEnvironment(model.Resources[4], model);

        await Assert.That(environment["ConnectionStrings__kurrentdb"]).IsEqualTo($"kurrentdb://{FakeConfiguration.Address}?tls=false");
        await Assert.That(environment["ConnectionStrings__catalogdb"])
            .IsEqualTo($"Host={FakeConfiguration.Host};Port={FakeConfiguration.Port};Username=postgres;Password={FakeConfiguration.Secret};Database=catalogdb");
        await Assert.That(environment["ConnectionStrings__blobs"]).StartsWith("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;");
    }

    [Test]
    public async Task AConditionalPartOfAConnectionString_IsLeftOut()
    {
        // Aspire 13.6's redis, as AddRedis("redis") writes it: the TLS part is a conditional
        // expression, named like a resource the model does not have. Guessed as a connection string
        // of its own, it was glued onto the password: "password=<secret>Host=127.0.0.1;Port=1;…".
        var model = new ProbeAspireModel
        {
            Resources =
            [
                new()
                {
                    Name = "redis", Type = "RedisResource",
                    Endpoints = [new() { Name = "tcp", Scheme = "redis", TargetPort = 6379 }],
                    ConnectionString = "{redis.bindings.tcp.host}:{redis.bindings.tcp.port},password={redis-password.value}{cond-redis-bindings-tcp-tlsenabled-d148d83a.connectionString}",
                },
                new() { Name = "api", Type = "ProjectResource", Environment = new() { ["ConnectionStrings__redis"] = "{redis.connectionString}" } },
            ],
        };

        var (environment, _) = AspireModel.ProbeEnvironment(model.Resources[1], model);

        await Assert.That(environment["ConnectionStrings__redis"]).IsEqualTo($"{FakeConfiguration.Address},password={FakeConfiguration.Secret}");
    }

    [Test]
    public async Task TheFakeSecret_IsValidBase64OfAtLeast32Bytes_AndEveryPasswordIsIt()
    {
        await Assert.That(Convert.FromBase64String(FakeConfiguration.Secret).Length).IsGreaterThanOrEqualTo(32);
        await Assert.That(FakeConfiguration.ConnectionString("postgres", "db")).Contains($"Password={FakeConfiguration.Secret};");
        await Assert.That(FakeConfiguration.ConnectionString("rabbitmq", "bus")).Contains($":{FakeConfiguration.Secret}@");

        var model = new ProbeAspireModel
        {
            Resources =
            [
                new() { Name = "postgres", Type = "PostgresServerResource" },
                new()
                {
                    Name = "api", Type = "ProjectResource",
                    Environment = new() { ["DB_PASSWORD"] = "{postgres-password.value}", ["Jwt__Key"] = "{jwt-key.value}", ["ConnectionStrings__db"] = "{postgres.connectionString}" },
                },
            ],
        };

        var (environment, _) = AspireModel.ProbeEnvironment(model.Resources[1], model);

        await Assert.That(environment["DB_PASSWORD"]).IsEqualTo(FakeConfiguration.Secret);
        await Assert.That(environment["Jwt__Key"]).IsEqualTo(FakeConfiguration.Secret);
        await Assert.That(environment["ConnectionStrings__db"]).Contains($"Password={environment["DB_PASSWORD"]}");
    }

    [Test]
    public async Task AResourcesOwnUrls_AreOnLocalhost_OnePortEach()
    {
        var model = new ProbeAspireModel
        {
            Resources =
            [
                new()
                {
                    Name = "api", Type = "ProjectResource",
                    // Publish mode: endpoints without ports.
                    Endpoints = [new() { Name = "http", Scheme = "http" }, new() { Name = "https", Scheme = "https" }],
                    Environment = new() { ["ASPNETCORE_URLS"] = "{api.bindings.http.url};{api.bindings.https.url};{api.bindings.http.url}" },
                },
            ],
        };

        var (environment, _) = AspireModel.ProbeEnvironment(model.Resources[0], model);

        await Assert.That(environment["ASPNETCORE_URLS"]).IsEqualTo("http://localhost:5000;https://localhost:5001");
    }

    [Test]
    [Arguments("Password", "p@ss", "***")]
    [Arguments("Identity:ClientSecret", "s3cret", "***")]
    [Arguments("Jwt:SigningKey", "abc", "***")]
    [Arguments("Jwt:Keys:0", "abc", "***")]
    [Arguments("RABBITMQ_PASSWORD", "guest", "***")]
    [Arguments("MessageBroker__Password", "guest", "***")]
    [Arguments("Stripe:ApiKey", "sk_live", "***")]
    [Arguments("Auth:AccessToken", "t", "***")]
    [Arguments("Jwt:ValidateIssuerSigningKey", "true", "true")]
    [Arguments("Identity:Password:RequiredLength", "8", "8")]
    [Arguments("Logging:LogLevel:Default", "Information", "Information")]
    [Arguments("ConnectionStrings:db", "Host=db;Username=u;Password=123456aA*;Database=x", "Host=db;Username=u;Password=***;Database=x")]
    [Arguments("ConnectionStrings:sql", "Server=.;User Id=sa;Pwd=SwN12345678", "Server=.;User Id=sa;Pwd=***")]
    [Arguments("EventBus:Uri", "amqp://guest:guest@rabbitmq:5672", "amqp://guest:***@rabbitmq:5672")]
    [Arguments("ConnectionStrings:redis", "cache:6379,password=x,ssl=false", "cache:6379,password=***,ssl=false")]
    [Arguments("Services:Api", "http://localhost:5000", "http://localhost:5000")]
    // Every pair whose key names a secret, whatever the client calls it.
    [Arguments("ConnectionStrings:openai", "Endpoint=https://x.openai.azure.com/;Key=abc123secret", "Endpoint=https://x.openai.azure.com/;Key=***")]
    [Arguments("ConnectionStrings:milvus", "Endpoint=http://milvus:19530;Key=root:Milvus", "Endpoint=http://milvus:19530;Key=***")]
    [Arguments("ConnectionStrings:search", "Endpoint=https://h;ApiKey=k123456", "Endpoint=https://h;ApiKey=***")]
    [Arguments("ConnectionStrings:x", "Server=h;Token=t0k3n", "Server=h;Token=***")]
    [Arguments("ConnectionStrings:kafka", "broker:9092;SaslUsername=u;SaslPassword=p4ss", "broker:9092;SaslUsername=u;SaslPassword=***")]
    [Arguments("ConnectionStrings:bus", "Endpoint=sb://h/;SharedAccessKeyName=Root;SharedAccessKey=abc", "Endpoint=sb://h/;SharedAccessKeyName=Root;SharedAccessKey=***")]
    [Arguments("ConnectionStrings:blobs", "BlobEndpoint=https://a.blob.core.windows.net/;SharedAccessSignature=sv=2022-11-02&ss=b&sig=AbC%2Bd", "BlobEndpoint=https://a.blob.core.windows.net/;SharedAccessSignature=***")]
    [Arguments("Storage:ContainerUrl", "https://a.blob.core.windows.net/c?sv=2022&sig=AbCd", "https://a.blob.core.windows.net/c?sv=2022&sig=***")]
    [Arguments("ConnectionStrings:sql", "Server=.;Password=two words;Database=x", "Server=.;Password=***;Database=x")]
    public async Task Secrets_AreMaskedByNameAndInsideConnectionStrings(string key, string value, string expected) =>
        await Assert.That(Secrets.Mask(key, value)).IsEqualTo(expected);

    [Test]
    [Arguments("redis-server --requirepass s3cretValue --appendonly yes", "redis-server --requirepass *** --appendonly yes")]
    [Arguments("server --password 'p a s s' --port 5432", "server --password *** --port 5432")]
    [Arguments("mongod --auth --bind_ip_all", "mongod --auth --bind_ip_all")]
    [Arguments("app --api-key=abc123 --verbose", "app --api-key=*** --verbose")]
    [Arguments("docker run -e POSTGRES_PASSWORD=pw123456 postgres", "docker run -e POSTGRES_PASSWORD=*** postgres")]
    [Arguments("Failed: Host=db;Username=u;Password=hunter22;Database=x", "Failed: Host=db;Username=u;Password=***;Database=x")]
    [Arguments("connect to amqp://guest:guest@rabbit:5672 failed", "connect to amqp://guest:***@rabbit:5672 failed")]
    [Arguments("No such host is known (orders-db:5432)", "No such host is known (orders-db:5432)")]
    public async Task FreeText_IsMaskedByTheSameRule(string text, string expected) =>
        await Assert.That(Secrets.MaskText(text)).IsEqualTo(expected);

    [Test]
    public async Task FreeText_LosesEveryKnownSecretValue_SpacesAndAll()
    {
        var known = new HashSet<string>();
        Secrets.CollectSecrets("Redis__Password", "two words!", known);
        Secrets.CollectSecrets("ConnectionStrings:db", "Host=db;Password=Sup3rSecret", known);
        Secrets.CollectSecrets("Short__Password", "abc", known);
        Secrets.CollectSecrets("Feature__RequireKey", "true", known);

        await Assert.That(known).IsEquivalentTo(["two words!", "Sup3rSecret"]);
        await Assert.That(Secrets.MaskText("AUTH two words! rejected; login with Sup3rSecret failed; abc stays", known))
            .IsEqualTo("AUTH *** rejected; login with *** failed; abc stays");
    }

    [Test]
    public async Task TheDocument_MasksCommandsErrorsAndNotes_WithTheSecretsItsConfigurationHolds()
    {
        const string secret = "xyzRedisSecret1";
        var document = new InventoryDocument();
        document.Deployment.Services.Add(new DeploymentService
        {
            Name = "cache", Command = $"redis-server --appendonly yes --requirepass {secret}",
            Environment = new(StringComparer.Ordinal) { ["REDIS_PASSWORD"] = secret },
        });
        document.Deployment.Services.Add(new DeploymentService { Name = "worker", Command = $"worker --cache redis://cache:6379,{secret}" });
        document.Deployment.Notes.Add($"Service 'worker' ran with {secret}.");
        document.Hosts.Add(new HostInfo
        {
            Service = "api",
            Runtime = new ProbeOutput
            {
                Errors =
                [
                    new ProbeError { Stage = "startup", Message = $"NOAUTH: invalid password {secret}", Detail = $"thrown at X\ncaused by Y: {secret}" },
                    new ProbeError { Stage = "output", Message = "The last lines the application wrote.", Detail = $"info: connecting with {secret}" },
                ],
            },
        });
        document.Errors.Add(new InventoryError { Stage = "probe", Subject = "api", Message = $"built: NOAUTH: invalid password {secret}" });

        Inventory.Redact(document);

        var json = System.Text.Json.JsonSerializer.Serialize(document, JsonFormat.Options);
        await Assert.That(json).DoesNotContain(secret);
        await Assert.That(document.Deployment.Services[0].Command).IsEqualTo("redis-server --appendonly yes --requirepass ***");
    }

    [Test]
    public async Task TheDocument_MasksConfigurationEnvironmentAndCollisions_AfterComparingTheRealValues()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Api/Api.csproj", "Worker/Worker.csproj")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("Api/appsettings.json", """{ "ConnectionStrings": { "db": "Host=db;Password=apiPassword" }, "Identity": { "ClientSecret": "api-secret" } }"""),
            ("Api/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project", "environmentVariables": { "MessageBroker__Password": "SwN12345678" } } } }
            """),
            ("Worker/Worker.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")),
            ("Worker/appsettings.json", """{ "ConnectionStrings": { "db": "Host=db;Password=workerPassword" }, "Identity": { "ClientSecret": "worker-secret" } }"""));

        var document = await Fixture.StaticInventory(fixture.Root);

        var api = document.Hosts.Single(h => h.Service == "Api");
        await Assert.That(api.Configuration!["appsettings.json"]["ConnectionStrings:db"]).IsEqualTo("Host=db;Password=***");
        await Assert.That(api.Configuration!["appsettings.json"]["Identity:ClientSecret"]).IsEqualTo("***");
        await Assert.That(api.Environment!["MessageBroker__Password"]).IsEqualTo("***");
        await Assert.That(document.Deployment.Services.Single(s => s.Name == "Api").Environment!["MessageBroker__Password"]).IsEqualTo("***");

        // Still collisions — the values differ — but masked.
        var secret = document.Collisions.ConfigurationKeys.Single(c => c.Key == "Identity:ClientSecret");
        await Assert.That(secret.Values.Select(v => v.Value!)).IsEquivalentTo(["***", "***"]);
        var db = document.Collisions.ConfigurationKeys.Single(c => c.Key == "ConnectionStrings:db");
        await Assert.That(db.Values.Select(v => v.Value!)).IsEquivalentTo(["Host=db;Password=***", "Host=db;Password=***"]);

        var json = System.Text.Json.JsonSerializer.Serialize(document, JsonFormat.Options);
        await Assert.That(json).DoesNotContain("apiPassword").And.DoesNotContain("worker-secret").And.DoesNotContain("SwN12345678");
    }
}
