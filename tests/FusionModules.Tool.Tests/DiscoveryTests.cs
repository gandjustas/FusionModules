namespace FusionModules.Tool.Tests;

/// <summary>
/// How the tool decides what the deployment is: an AppHost first, then compose files, then the
/// executable projects; and for a project, by what kind of project it is. Static runs over
/// generated solutions — evaluation only, nothing restored or built.
/// </summary>
public class DiscoveryTests
{
    [Test]
    public async Task ADirectory_WithOneSolution_IsThatSolution()
    {
        using var fixture = new Fixture(("Shop.slnx", Fixture.Solution()), ("Api/Api.csproj", Fixture.Executable()));

        var input = InputResolver.Resolve(fixture.Root);

        await Assert.That(input.Solution).IsEqualTo(fixture["Shop.slnx"]);
        await Assert.That(input.Project).IsNull();
    }

    [Test]
    public async Task ADirectory_WithASlnAndASlnxOfOneName_IsTheSlnx()
    {
        using var fixture = new Fixture(("Shop.sln", ""), ("Shop.slnx", Fixture.Solution()));

        await Assert.That(InputResolver.Resolve(fixture.Root).Solution).IsEqualTo(fixture["Shop.slnx"]);
    }

    [Test]
    public async Task ADirectory_WithNoSolution_IsItsOnlyProject()
    {
        using var fixture = new Fixture(("Api.csproj", Fixture.Executable()));

        await Assert.That(InputResolver.Resolve(fixture.Root).Project).IsEqualTo(fixture["Api.csproj"]);
    }

    [Test]
    public async Task ADirectory_WithTwoSolutions_AsksForOne()
    {
        using var fixture = new Fixture(("A.slnx", Fixture.Solution()), ("B.slnx", Fixture.Solution()));

        var error = Assert.Throws<InventoryInputException>(() => InputResolver.Resolve(fixture.Root));

        await Assert.That(error.Message).Contains("several solutions");
    }

    [Test]
    public async Task ADirectory_WithNoSolutionAtItsTop_IsTheOneSolutionBelowIt()
    {
        using var fixture = new Fixture(
            ("README.md", "# shop"),
            ("src/eshop-microservices.sln", ""),
            ("src/Services/Api/Api.csproj", Fixture.Executable()),
            // Not where solutions live: packages, build output, dot-folders, and deeper than three levels.
            ("node_modules/pkg/Pkg.sln", ""),
            ("src/Services/Api/bin/Debug/Copy.sln", ""),
            (".vs/Shop.sln", ""),
            ("a/b/c/d/TooDeep.sln", ""));

        var input = InputResolver.Resolve(fixture.Root);

        await Assert.That(input.Solution).IsEqualTo(fixture["src/eshop-microservices.sln"]);
        await Assert.That(input.Root).IsEqualTo(fixture["src"]);
    }

    [Test]
    public async Task ADirectory_WithSeveralSolutionsBelowIt_ListsThem()
    {
        using var fixture = new Fixture(("services/A.slnx", Fixture.Solution()), ("tools/build/B.sln", ""), ("tools/build/B.slnx", Fixture.Solution()));

        var error = Assert.Throws<InventoryInputException>(() => InputResolver.Resolve(fixture.Root));

        await Assert.That(error.Message).Contains("services/A.slnx").And.Contains("tools/build/B.slnx").And.DoesNotContain("B.sln,");
    }

    [Test]
    public async Task ASolutionFilter_IsReadAsJson_WithEitherSeparator()
    {
        using var fixture = new Fixture(
            ("Shop.sln", ""),
            ("src/Api/Api.csproj", Fixture.Executable()),
            ("src/Worker/Worker.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")),
            ("src/Admin/Admin.csproj", Fixture.Executable()),
            // The solution relative to the filter, the projects relative to the solution.
            ("filters/services.slnf", """
                {
                  "solution": {
                    "path": "../Shop.sln",
                    "projects": [ "src/Api/Api.csproj", "src\\Worker\\Worker.csproj" ]
                  }
                }
                """));

        var document = await Fixture.StaticInventory(fixture["filters/services.slnf"]);

        await Assert.That(document.Projects.Where(p => p.InSolution).Select(p => p.Name)).IsEquivalentTo(["Api", "Worker"]);
    }

    [Test]
    public async Task ComposeServicesThatOnlyNameAnImage_AreTiedToProjectsByName()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("src/Catalog.Api/Catalog.Api.csproj", "src/Ordering/Ordering.csproj", "src/Catalog.Worker.Outbox/Catalog.Worker.Outbox.csproj", "src/YarpApiGateway/YarpApiGateway.csproj")),
            ("src/Catalog.Api/Catalog.Api.csproj", Fixture.Executable()),
            ("src/Ordering/Ordering.csproj", Fixture.Executable()),
            // The image is named after what the Dockerfile beside the project runs.
            ("src/Ordering/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nENTRYPOINT [\"dotnet\", \"Orders.Service.dll\"]\n"),
            ("src/Catalog.Worker.Outbox/Catalog.Worker.Outbox.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")),
            ("src/YarpApiGateway/YarpApiGateway.csproj", Fixture.Executable()),
            // Built by CI: images only, no build.
            ("docker-compose.yml", """
                services:
                  catalog-api:
                    image: ${DOCKERHUB_USERNAME}/catalog-api:latest
                  catalog_worker_outbox:
                    image: ${DOCKERHUB_USERNAME}/catalog-worker-outbox:latest
                  orders:
                    image: registry.example.com:5000/team/orders-service:1.2@sha256:abc
                  api-gateway:
                    image: ${DOCKERHUB_USERNAME}/api-gateway:latest
                  catalogdb:
                    image: postgres:16
                """));

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"], "--no-docker");

        await Assert.That(document.Deployment.Kind).IsEqualTo("compose");
        var services = document.Deployment.Services.ToDictionary(s => s.Name);
        await Assert.That((services["catalog-api"].Kind, services["catalog-api"].Project, services["catalog-api"].MappedBy))
            .IsEqualTo(("project", "src/Catalog.Api/Catalog.Api.csproj", "service-name"));
        await Assert.That(services["catalog_worker_outbox"].Project).IsEqualTo("src/Catalog.Worker.Outbox/Catalog.Worker.Outbox.csproj");
        await Assert.That((services["orders"].Project, services["orders"].MappedBy)).IsEqualTo(("src/Ordering/Ordering.csproj", "image-name"));
        await Assert.That(services["orders"].Evidence).Contains("Orders.Service.dll");
        await Assert.That(services["api-gateway"].Kind).IsEqualTo("container");
        await Assert.That((services["catalogdb"].Kind, services["catalogdb"].Type)).IsEqualTo(("infrastructure", "postgres"));
        await Assert.That(document.Deployment.Notes).Contains(n => n.Contains("api-gateway"));
        await Assert.That(document.Hosts.Select(h => h.Service)).IsEquivalentTo(["catalog-api", "catalog_worker_outbox", "orders"]);
    }

    [Test]
    public async Task ComposeFilesThatMapNoService_FallBackToTheExecutables_AndSaySo()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Api/Api.csproj")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("docker-compose.yml", "services:\n  shop:\n    image: someone/storefront:1\n  db:\n    image: postgres:16\n"));

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"], "--no-docker");

        await Assert.That(document.Deployment.Kind).IsEqualTo("executables");
        await Assert.That(document.Deployment.Services.Select(s => s.Name)).IsEquivalentTo(["Api"]);
        await Assert.That(document.Deployment.Notes).Contains(n => n.Contains("map none of their services"));
        await Assert.That(document.Hosts.Select(h => h.Service)).IsEquivalentTo(["Api"]);
    }

    [Test]
    public async Task AnExplicitComposeFile_WinsOverTheAppHost()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("AppHost/AppHost.csproj", "Api/Api.csproj")),
            ("AppHost/AppHost.csproj", Fixture.Executable("Microsoft.NET.Sdk", "  <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("deploy/compose.yaml", "services:\n  api:\n    image: shop/api:1\n"));

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"], "--no-docker", "--compose-file", fixture["deploy/compose.yaml"]);

        await Assert.That(document.Deployment.Kind).IsEqualTo("compose");
        await Assert.That(document.Deployment.Services.Single().Project).IsEqualTo("Api/Api.csproj");
        await Assert.That(document.Deployment.Notes).Contains(n => n.Contains("not the AppHost AppHost"));
    }

    [Test]
    public async Task AnAppHost_IsTheDeployment_EvenBesideComposeFiles()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("AppHost/AppHost.csproj", "Api/Api.csproj", "Api.Tests/Api.Tests.csproj")),
            // IsAspireHost is what Aspire.AppHost.Sdk sets; set by hand, evaluation sees the same.
            ("AppHost/AppHost.csproj", Fixture.Executable("Microsoft.NET.Sdk", """
              <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Api/Api.csproj" /></ItemGroup>
            """)),
            ("Api/Api.csproj", Fixture.Executable()),
            // A functional test that starts Aspire resources of its own is not a second AppHost.
            ("Api.Tests/Api.Tests.csproj", Fixture.Executable("Microsoft.NET.Sdk", """
              <PropertyGroup><IsTestProject>true</IsTestProject><IsAspireHost>true</IsAspireHost></PropertyGroup>
            """)),
            ("docker-compose.yml", "services:\n  api:\n    build: ./Api\n"));

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"]);

        await Assert.That(document.Deployment.Kind).IsEqualTo("aspire");
        await Assert.That(document.Deployment.Sources).IsEquivalentTo(["AppHost/AppHost.csproj"]);
        await Assert.That(document.Deployment.Notes).DoesNotContain(n => n.Contains("Several AppHosts"));
        // Static: no model, so the referenced projects stand in for the services.
        await Assert.That(document.Deployment.ReadBy).IsEqualTo("project-references");
        await Assert.That(document.Deployment.Services.Select(s => s.Project!)).IsEquivalentTo(["Api/Api.csproj"]);
        await Assert.That(document.Projects.Single(p => p.Name == "Api.Tests").Kind).IsEqualTo("test");
    }

    [Test]
    public async Task ComposeFiles_AreTheDeployment_WhenThereIsNoAppHost()
    {
        using var fixture = new Fixture(
            ("src/Shop.slnx", Fixture.Solution("Api/Api.csproj", "Worker/Worker.csproj")),
            ("src/Api/Api.csproj", Fixture.Executable()),
            ("src/Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n"),
            ("src/Worker/Worker.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")),
            // One level above the solution, inside the repository: still "near" it.
            (".git/HEAD", "ref: refs/heads/main\n"),
            ("docker-compose.yml", """
            services:
              api:
                build:
                  context: src
                  dockerfile: Api/Dockerfile
              db:
                image: postgres:16
            """));

        var document = await Fixture.StaticInventory(fixture["src/Shop.slnx"], "--no-docker");

        await Assert.That(document.Deployment.Kind).IsEqualTo("compose");
        await Assert.That(document.Deployment.Sources).IsEquivalentTo(["../docker-compose.yml"]);
        var api = document.Deployment.Services.Single(s => s.Name == "api");
        await Assert.That(api.Project).IsEqualTo("Api/Api.csproj");
        await Assert.That(api.Build!.MappedBy).IsEqualTo("entrypoint");
        var db = document.Deployment.Services.Single(s => s.Name == "db");
        await Assert.That(db.Kind).IsEqualTo("infrastructure");
        await Assert.That(db.Type).IsEqualTo("postgres");
        await Assert.That(document.Hosts.Select(h => h.Service)).IsEquivalentTo(["api"]);
    }

    [Test]
    public async Task ExecutableProjects_AreTheDeployment_WithoutOrchestration_AndSaySo()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Api/Api.csproj", "Worker/Worker.csproj", "Lib/Lib.csproj", "Api.Tests/Api.Tests.csproj")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("Api/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5100",
                                      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "Feature__On": "true" } } } }
            """),
            ("Api/appsettings.json", """{ "Shared": { "Name": "api" } }"""),
            ("Worker/Worker.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")),
            ("Worker/appsettings.json", """{ "Shared": { "Name": "worker" } }"""),
            ("Lib/Lib.csproj", Fixture.Project()),
            ("Api.Tests/Api.Tests.csproj", Fixture.Executable("Microsoft.NET.Sdk", """
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" /></ItemGroup>
            """)));

        var document = await Fixture.StaticInventory(fixture.Root);

        await Assert.That(document.Deployment.Kind).IsEqualTo("executables");
        await Assert.That(document.Deployment.Services.Select(s => s.Name)).IsEquivalentTo(["Api", "Worker"]);
        await Assert.That(document.Deployment.Notes).Contains(n => n.Contains("each of the 2 executable projects"));

        var api = document.Deployment.Services.Single(s => s.Name == "Api");
        await Assert.That(api.LaunchProfile).IsEqualTo("http");
        await Assert.That(api.Environment!["Feature__On"]).IsEqualTo("true");
        await Assert.That(api.Ports!.Single().Port).IsEqualTo(5100);
        await Assert.That(document.Projects.Single(p => p.Name == "Worker").Kind).IsEqualTo("worker");

        // Static runs still compare configuration.
        var collision = document.Collisions.ConfigurationKeys.Single(c => c.Key == "Shared:Name");
        await Assert.That(collision.Source).IsEqualTo("appsettings.json");
        await Assert.That(collision.Values.Select(v => v.Value!)).IsEquivalentTo(["api", "worker"]);
    }

    [Test]
    public async Task ALibraryProject_IsStaticInformationOnly()
    {
        using var fixture = new Fixture(("Lib/Lib.csproj", Fixture.Project()));

        var document = await Fixture.StaticInventory(fixture["Lib/Lib.csproj"]);

        await Assert.That(document.Deployment.Kind).IsEqualTo("project");
        await Assert.That(document.Deployment.Services).IsEmpty();
        await Assert.That(document.Deployment.Notes).Contains(n => n.Contains("library"));
        await Assert.That(document.Hosts).IsEmpty();
    }

    [Test]
    public async Task AWebProject_IsOneServiceOfItsOwn()
    {
        using var fixture = new Fixture(("Api/Api.csproj", Fixture.Executable()));

        var document = await Fixture.StaticInventory(fixture["Api/Api.csproj"]);

        await Assert.That(document.Deployment.Kind).IsEqualTo("project");
        await Assert.That(document.Deployment.Services.Single().Type).IsEqualTo("web");
        await Assert.That(document.Hosts.Single().Project).IsEqualTo("Api.csproj");
    }

    [Test]
    [Arguments("net6.0", "Microsoft.AspNetCore.App", null, "net6.0")]
    [Arguments("net5.0", "Microsoft.AspNetCore.App", null, "net5.0")]
    [Arguments("netcoreapp3.1", "Microsoft.AspNetCore.App", null, "netcoreapp3.1")]
    [Arguments("net8.0", "Microsoft.AspNetCore.App", null, null)]
    [Arguments("net10.0", "Microsoft.NETCore.App", "does not use Microsoft.Extensions.Hosting", null)]
    public async Task AnOlderHost_IsProbedRolledForward_NotSkipped(string tfm, string framework, string? whyNot, string? rollForwardFrom)
    {
        using var fixture = new Fixture(
            ("bin/App.dll", ""),
            ("bin/App.deps.json", """{ "libraries": {} }"""),
            ("bin/App.runtimeconfig.json", $$"""{ "runtimeOptions": { "tfm": "{{tfm}}", "framework": { "name": "{{framework}}", "version": "6.0.0" } } }"""));

        var check = HostProber.Check(fixture["bin/App.dll"], tfm);

        await Assert.That(check.RollForwardFrom).IsEqualTo(rollForwardFrom);
        if (whyNot is null)
        {
            await Assert.That(check.WhyNot).IsNull();
        }
        else
        {
            await Assert.That(check.WhyNot).Contains(whyNot);
        }
    }

    [Test]
    [Arguments("net48", true)]
    [Arguments("net472", true)]
    [Arguments("net8.0", false)]
    [Arguments("netcoreapp3.1", false)]
    [Arguments("netstandard2.0", false)]
    public async Task DotNetFramework_IsTheOnlyRuntimeThatCannotBeProbed(string tfm, bool expected) =>
        await Assert.That(HostProber.IsNetFramework(tfm)).IsEqualTo(expected);

    [Test]
    [Arguments("C:\\src\\AppHost\\AppHost.csproj : error NETSDK1228: This project depends on Aspire 8 preview. [C:\\src\\AppHost\\AppHost.csproj]", "NETSDK1228: This project depends on Aspire 8 preview.")]
    [Arguments("Program.cs(3,1): error CS1002: ; expected [C:\\src\\AppHost\\AppHost.csproj::TargetFramework=net10.0]", "CS1002: ; expected")]
    public async Task TheFirstBuildError_IsItsCodeAndMessage(string line, string expected) =>
        await Assert.That(Inventory.FirstBuildError([line])).IsEqualTo(expected);

    [Test]
    public async Task AnEnvForOneHost_ReachesThatHostOnly()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Api/Api.csproj", "Worker/Worker.csproj")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("Worker/Worker.csproj", Fixture.Executable("Microsoft.NET.Sdk.Worker")));

        var document = await Fixture.StaticInventory(fixture.Root,
            "--env", "Api:ConnectionStrings__common=Host=db", "--env", "Shared=1", "--env", "Logging:LogLevel:Default=Debug");

        var api = document.Hosts.Single(h => h.Service == "Api").Environment!;
        var worker = document.Hosts.Single(h => h.Service == "Worker").Environment!;
        await Assert.That(api["ConnectionStrings__common"]).IsEqualTo("Host=db");
        await Assert.That(worker.ContainsKey("ConnectionStrings__common")).IsFalse();
        await Assert.That((api["Shared"], worker["Shared"])).IsEqualTo(("1", "1"));
        // A scope that names no host is part of the variable's name: a configuration key with colons.
        await Assert.That((api["Logging:LogLevel:Default"], worker["Logging:LogLevel:Default"])).IsEqualTo(("Debug", "Debug"));
    }

    [Test]
    public async Task NothingDeployable_IsSaid()
    {
        using var fixture = new Fixture(("Shop.slnx", Fixture.Solution("Lib/Lib.csproj")), ("Lib/Lib.csproj", Fixture.Project()));

        var document = await Fixture.StaticInventory(fixture.Root);

        await Assert.That(document.Deployment.Kind).IsEqualTo("none");
    }
}
