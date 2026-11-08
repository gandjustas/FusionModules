namespace FusionModules.Tool.Tests;

/// <summary>
/// Project facts come from MSBuild evaluation, so whatever MSBuild would see — central package
/// versions, conditions on the target framework, references declared in imported files — is what
/// the inventory reports.
/// </summary>
public class ProjectEvaluationTests
{
    private static Fixture Solution() => new(
        ("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Polly" Version="8.5.0" />
                <PackageVersion Include="Dapper" Version="2.1.35" />
                <PackageVersion Include="Humanizer.Core" Version="2.14.1" />
              </ItemGroup>
            </Project>
            """),
        // A reference declared in an imported file, not in the project: regexes over the csproj miss it.
        ("Directory.Build.props", """
            <Project>
              <ItemGroup Condition="'$(MSBuildProjectName)' == 'Api'">
                <ProjectReference Include="$(MSBuildThisFileDirectory)Domain/Domain.csproj" />
              </ItemGroup>
            </Project>
            """),
        ("Shop.slnx", Fixture.Solution("Api/Api.csproj", "Domain/Domain.csproj")),
        ("Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                <AssemblyName>Shop.Api</AssemblyName>
                <UserSecretsId>shop-api</UserSecretsId>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Polly" />
                <PackageReference Include="Dapper" VersionOverride="2.1.66" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Humanizer.Core" />
              </ItemGroup>
            </Project>
            """),
        ("Domain/Domain.csproj", Fixture.Project(body: """
              <ItemGroup><ProjectReference Include="../Kernel/Kernel.csproj" /></ItemGroup>
            """)),
        // Referenced, but not in the solution.
        ("Kernel/Kernel.csproj", Fixture.Project()));

    [Test]
    public async Task Properties_ItemsAndCentralVersions_ComeFromEvaluation()
    {
        using var fixture = Solution();

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"]);
        var api = document.Projects.Single(p => p.Name == "Api");

        await Assert.That(api.AssemblyName).IsEqualTo("Shop.Api");
        await Assert.That(api.Sdk).IsEqualTo("Microsoft.NET.Sdk.Web");
        await Assert.That(api.Kind).IsEqualTo("web");
        await Assert.That(api.OutputType).IsEqualTo("Exe");
        await Assert.That(api.TargetFrameworks).IsEquivalentTo(["net8.0", "net10.0"]);
        await Assert.That(api.Properties!["UserSecretsId"]).IsEqualTo("shop-api");

        var packages = api.PackageReferences.Where(p => p.Implicit is null).ToDictionary(p => p.Id, p => p.Requested);
        await Assert.That(packages["Polly"]).IsEqualTo("8.5.0");
        await Assert.That(packages["Dapper"]).IsEqualTo("2.1.66");
        // Conditioned on net10.0: there because the highest framework is the one evaluated.
        await Assert.That(packages["Humanizer.Core"]).IsEqualTo("2.14.1");
    }

    [Test]
    public async Task ProjectReferences_AreResolvedTransitively_IncludingProjectsOutsideTheSolution()
    {
        using var fixture = Solution();

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"]);
        var api = document.Projects.Single(p => p.Name == "Api");

        await Assert.That(api.ProjectReferences).IsEquivalentTo(["Domain/Domain.csproj"]);
        await Assert.That(api.TransitiveProjectReferences).IsEquivalentTo(["Domain/Domain.csproj", "Kernel/Kernel.csproj"]);

        var kernel = document.Projects.Single(p => p.Name == "Kernel");
        await Assert.That(kernel.InSolution).IsFalse();
        await Assert.That(kernel.Kind).IsEqualTo("library");
    }

    [Test]
    public async Task AFramework_CanBeChosen()
    {
        using var fixture = Solution();

        var document = await Fixture.StaticInventory(fixture["Shop.slnx"], "--framework", "net8.0");

        var api = document.Projects.Single(p => p.Name == "Api");
        await Assert.That(api.PackageReferences.Select(p => p.Id)).DoesNotContain("Humanizer.Core");
        await Assert.That(document.Hosts.Single().TargetFramework).IsEqualTo("net8.0");
    }

    [Test]
    public async Task ABrokenProject_IsAnErrorOfItsOwn_NotOfTheInventory()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Api/Api.csproj", "Broken/Broken.csproj")),
            ("Api/Api.csproj", Fixture.Executable()),
            ("Broken/Broken.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"));

        var document = await Fixture.StaticInventoryWithErrors(fixture["Shop.slnx"]);

        await Assert.That(document.Projects.Single(p => p.Name == "Broken").Error).IsNotNull();
        await Assert.That(document.Errors).Contains(e => e.Stage == "evaluate" && e.Subject == "Broken/Broken.csproj");
        await Assert.That(document.Deployment.Services.Single().Name).IsEqualTo("Api");
    }

    [Test]
    [Arguments(new[] { "net8.0", "net10.0" }, null, "net10.0")]
    [Arguments(new[] { "net10.0", "net9.0" }, null, "net10.0")]
    [Arguments(new[] { "net8.0", "net10.0" }, "net8.0", "net8.0")]
    [Arguments(new[] { "net10.0-android", "net9.0" }, null, "net9.0")]
    public async Task TheHighestPlainFramework_IsChosen(string[] frameworks, string? requested, string expected) =>
        await Assert.That(Frameworks.Choose(frameworks, requested)).IsEqualTo(expected);
}
