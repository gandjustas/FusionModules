using FusionModules.Tool.Probe;

namespace FusionModules.Tool.Tests;

/// <summary>Compose files read without docker, and images tied back to the projects they build.</summary>
public class ComposeTests
{
    private const string BaseFile = """
        services:
          catalog.api:
            image: ${DOCKER_REGISTRY-}catalogapi
            build:
              context: .
              dockerfile: Services/Catalog/Catalog.API/Dockerfile
          catalogdb:
            image: postgres
          broker:
            image: rabbitmq:management
          worker:
            build: Services/Worker
            deploy:
              replicas: 3
        """;

    private const string OverrideFile = """
        services:
          catalog.api:
            environment:
              - ASPNETCORE_ENVIRONMENT=Development
              - ConnectionStrings__Database=Server=catalogdb;Port=5432;Database=${CATALOG_DB:-CatalogDb};Password=$$secret
            depends_on:
              - catalogdb
            ports:
              - "6000:8080"
              - "127.0.0.1:6060:8081/tcp"
          catalogdb:
            environment:
              POSTGRES_USER: postgres
            ports:
              - 5432
        """;

    [Test]
    public async Task OverrideFiles_MergeByService()
    {
        using var fixture = new Fixture(("docker-compose.yml", BaseFile), ("docker-compose.override.yml", OverrideFile));

        var services = ComposeReader.FromYaml([fixture["docker-compose.yml"], fixture["docker-compose.override.yml"]]).ToDictionary(s => s.Name);

        var api = services["catalog.api"];
        await Assert.That(api.Image).IsEqualTo("catalogapi");
        await Assert.That(api.Dockerfile).IsEqualTo(fixture["Services/Catalog/Catalog.API/Dockerfile"]);
        await Assert.That(api.Environment["ConnectionStrings__Database"]).IsEqualTo("Server=catalogdb;Port=5432;Database=CatalogDb;Password=$secret");
        await Assert.That(api.DependsOn).IsEquivalentTo(["catalogdb"]);
        await Assert.That(api.Ports.Select(p => (p.Port, p.TargetPort))).IsEquivalentTo([((int?)6000, (int?)8080), (6060, 8081)]);

        await Assert.That(services["catalogdb"].Environment["POSTGRES_USER"]).IsEqualTo("postgres");
        await Assert.That(services["catalogdb"].Ports.Single().TargetPort).IsEqualTo(5432);
        await Assert.That(services["worker"].Replicas).IsEqualTo(3);
        await Assert.That(services["worker"].Dockerfile).IsEqualTo(fixture["Services/Worker/Dockerfile"]);
    }

    [Test]
    public async Task DotEnv_FillsInterpolation()
    {
        using var fixture = new Fixture(
            (".env", "CATALOG_DB=FromDotEnv\n"),
            ("docker-compose.yml", BaseFile),
            ("docker-compose.override.yml", OverrideFile));

        var api = ComposeReader.FromYaml([fixture["docker-compose.yml"], fixture["docker-compose.override.yml"]]).Single(s => s.Name == "catalog.api");

        await Assert.That(api.Environment["ConnectionStrings__Database"]).Contains("Database=FromDotEnv;");
    }

    [Test]
    public async Task DockerComposeConfigJson_IsRead()
    {
        const string json = """
            {
              "name": "src",
              "services": {
                "basket.api": {
                  "build": { "context": "C:\\src", "dockerfile": "Services/Basket/Dockerfile" },
                  "depends_on": { "basketdb": { "condition": "service_started", "required": true } },
                  "environment": { "ConnectionStrings__Redis": "distributedcache:6379", "EMPTY": null },
                  "image": "basketapi",
                  "ports": [ { "mode": "ingress", "target": 8080, "published": "6001", "protocol": "tcp" } ],
                  "deploy": { "replicas": 2 }
                }
              }
            }
            """;

        var basket = ComposeReader.FromConfigJson(json).Single();

        await Assert.That(basket.Environment["ConnectionStrings__Redis"]).IsEqualTo("distributedcache:6379");
        await Assert.That(basket.Environment["EMPTY"]).IsNull();
        await Assert.That(basket.DependsOn).IsEquivalentTo(["basketdb"]);
        await Assert.That(basket.Ports.Single().Port).IsEqualTo(6001);
        await Assert.That(basket.Replicas).IsEqualTo(2);
        await Assert.That(basket.Dockerfile).EndsWith(Path.Combine("Services", "Basket", "Dockerfile"));
    }

    [Test]
    public async Task ADockerfile_IsTiedToTheAssemblyItsEntrypointRuns()
    {
        using var fixture = new Fixture(("docker/Dockerfile", """
            FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
            WORKDIR /src
            COPY ["Services/Catalog/Catalog.API/Catalog.API.csproj", "Services/Catalog/Catalog.API/"]
            RUN dotnet restore "./Services/Catalog/Catalog.API/./Catalog.API.csproj"
            RUN dotnet publish "./Catalog.API.csproj" -c Release \
                -o /app/publish /p:UseAppHost=false
            FROM mcr.microsoft.com/dotnet/aspnet:8.0
            ENTRYPOINT ["dotnet", "Catalog.Api.Host.dll"]
            """));
        var projects = new[]
        {
            Project("Services/Catalog/Catalog.API/Catalog.API.csproj", "Catalog.Api.Host"),
            Project("Services/Basket/Basket.API/Basket.API.csproj", "Basket.API"),
        };

        var (project, mappedBy, _) = DockerfileReader.Map(fixture["docker/Dockerfile"], projects);

        await Assert.That(project).IsSameReferenceAs(projects[0]);
        await Assert.That(mappedBy).IsEqualTo("entrypoint");
    }

    [Test]
    public async Task WithoutAKnownEntrypoint_ThePublishedProjectDecides()
    {
        using var fixture = new Fixture(("Dockerfile", """
            FROM mcr.microsoft.com/dotnet/sdk:8.0
            RUN dotnet publish src/Ordering/Ordering.API.csproj -o /app && echo done
            ENTRYPOINT ["/app/Ordering"]
            """));
        var projects = new[] { Project("other/Ordering.API.csproj", "Ordering.API"), Project("src/Ordering/Ordering.API.csproj", "Ordering.API") };

        var (project, mappedBy, evidence) = DockerfileReader.Map(fixture["Dockerfile"], projects);

        await Assert.That(project).IsSameReferenceAs(projects[1]);
        await Assert.That(mappedBy).IsEqualTo("publish");
        await Assert.That(evidence).IsEqualTo("src/Ordering/Ordering.API.csproj");
    }

    [Test]
    public async Task OtherwiseTheProjectBesideTheDockerfile()
    {
        using var fixture = new Fixture(("Api/Dockerfile", "FROM scratch\nCMD [\"./run.sh\"]\n"));
        var beside = Project("Api/Api.csproj", "Api");
        beside.FullPath = fixture["Api/Api.csproj"];

        var (project, mappedBy, _) = DockerfileReader.Map(fixture["Api/Dockerfile"], [beside]);

        await Assert.That(project).IsSameReferenceAs(beside);
        await Assert.That(mappedBy).IsEqualTo("dockerfile-directory");
    }

    [Test]
    [Arguments("library")]
    [Arguments("test")]
    public async Task ButNeverALibraryOrTestProjectBesideIt(string kind)
    {
        using var fixture = new Fixture(("Api/Dockerfile", "FROM scratch\nCMD [\"./run.sh\"]\n"));
        var beside = Project("Api/Api.csproj", "Api");
        beside.FullPath = fixture["Api/Api.csproj"];
        beside.Kind = kind;

        var (project, mappedBy, _) = DockerfileReader.Map(fixture["Api/Dockerfile"], [beside]);

        await Assert.That(project).IsNull();
        await Assert.That(mappedBy).IsEqualTo("none");
    }

    [Test]
    [Arguments("postgres", "postgres")]
    [Arguments("ankane/pgvector:latest", "postgres")]
    [Arguments("mcr.microsoft.com/mssql/server:2022-latest", "sqlserver")]
    [Arguments("rabbitmq:3-management", "rabbitmq")]
    [Arguments("redis", "redis")]
    [Arguments("registry.example.com:5000/team/thing:1.0", "container")]
    [Arguments("jaegertracing/all-in-one:1.60", "jaeger")]
    [Arguments("otel/opentelemetry-collector-contrib", "opentelemetry")]
    [Arguments("datalust/seq", "seq")]
    [Arguments("hashicorp/consul:1.19", "consul")]
    [Arguments("valkey/valkey:8", "redis")]
    [Arguments("kurrentplatform/kurrentdb:25.0", "kurrentdb")]
    [Arguments("mcr.microsoft.com/azure-sql-edge", "sqlserver")]
    [Arguments("mcr.microsoft.com/azure-storage/azurite", "azure-storage")]
    [Arguments("confluentinc/cp-kafka:7.6.0", "kafka")]
    [Arguments("container-registry.oracle.com/database/free", "oracle")]
    // A word of the table inside another word is not the word: these are someone's own services.
    [Arguments("acme/hotel-api:1.0", "container")]
    [Arguments("acme/sequencer", "container")]
    [Arguments("consultation-svc", "container")]
    [Arguments("acme/storage-api", "container")]
    [Arguments("acme/order-queue-processor", "container")]
    [Arguments("acme/mongoose-gateway", "container")]
    public async Task Images_AreClassified(string image, string expected) =>
        await Assert.That(Infrastructure.Classify(image)).IsEqualTo(expected);

    [Test]
    public async Task AServiceWhoseImageHasAnInfrastructureWordInside_IsStillTiedToItsProject()
    {
        var hotel = Project("Hotel.Api/Hotel.Api.csproj", "Hotel.Api");

        var (project, mappedBy, _) = ImageMapper.Map("hotel-api", "registry.example.com/acme/hotel-api:latest", [hotel]);

        await Assert.That(project).IsSameReferenceAs(hotel);
        await Assert.That(mappedBy).IsEqualTo("service-name");
    }

    [Test]
    [Arguments("ValkeyResource", "redis")]
    [Arguments("GarnetResource", "redis")]
    [Arguments("KurrentDBResource", "kurrentdb")]
    [Arguments("AzureServiceBusQueueResource", "azure-servicebus")]
    [Arguments("AzureBlobStorageResource", "azure-storage")]
    [Arguments("OllamaModelResource", "ollama")]
    [Arguments("ExecutableResource", "other")]
    public async Task ResourceTypes_AreReadByTheSameTable(string type, string expected) =>
        await Assert.That(FakeConfiguration.KindOfResourceType(type)).IsEqualTo(expected);

    [Test]
    [Arguments("Aspire:Confluent:Kafka", "events", "kafka")]
    [Arguments("Aspire:StackExchange:Redis", "cache", "redis")]
    [Arguments("Aspire:Microsoft:Data:SqlClient", "orders", "sqlserver")]
    [Arguments("Aspire:Azure:Data:Tables", "tables", "azure-storage")]
    [Arguments("Aspire:Search", "valkey", "redis")]
    public async Task AMissingConnectionStringsKind_IsItsIntegrationsOrElseItsNames(string section, string name, string expected)
    {
        var message = $"ConnectionString is missing. It should be provided in 'ConnectionStrings:{name}' or under the 'ConnectionString' key in '{section}' configuration section.";

        var missing = FakeConfiguration.MissingConnectionString([ProbeError.Of("build", message)]);

        await Assert.That(missing).IsEqualTo((name, expected));
    }

    [Test]
    [Arguments("", "mysql", "mysql")]
    [Arguments(":latest", "rabbitmq", "rabbitmq")]
    [Arguments("someone/catalog-api:latest", "mysql", "container")]
    public async Task AnImageLeftEmpty_IsClassifiedByItsServiceName(string image, string service, string expected) =>
        await Assert.That(Infrastructure.Classify(image, service)).IsEqualTo(expected);

    [Test]
    [Arguments("${DOCKERHUB_USERNAME}/catalog-api:latest", "catalog-api")]
    [Arguments("/catalog-api:latest", "catalog-api")]
    [Arguments("registry.example.com:5000/team/orders:1.2@sha256:abc", "orders")]
    [Arguments("basketapi", "basketapi")]
    public async Task AnImagesName_IsWithoutRegistryAndTag(string image, string expected) =>
        await Assert.That(ImageMapper.ImageName(image)).IsEqualTo(expected);

    [Test]
    public async Task ComposeFiles_AreFoundBelowTheSolution_WithTheirOverride()
    {
        using var fixture = new Fixture(
            ("App.slnx", "<Solution />"),
            ("deploy/docker-compose.yml", "services: {}"),
            ("deploy/docker-compose.override.yml", "services: {}"),
            ("deploy/docker-compose.prod.yml", "services: {}"));

        var files = ComposeDiscovery.Discover(fixture.Root)!;

        await Assert.That(files.Files).IsEquivalentTo([fixture["deploy/docker-compose.yml"], fixture["deploy/docker-compose.override.yml"]]);
        await Assert.That(files.Others).IsEquivalentTo([fixture["deploy/docker-compose.prod.yml"]]);
    }

    private static ProjectInfo Project(string path, string assembly) => new()
    {
        FullPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), path)),
        Name = Path.GetFileNameWithoutExtension(path),
        AssemblyName = assembly,
        Kind = "web",
    };
}
