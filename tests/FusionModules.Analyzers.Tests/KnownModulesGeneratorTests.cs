using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static FusionModules.Analyzers.Tests.GeneratorTest;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// The list a design-time factory would otherwise keep by hand.
/// </summary>
public class KnownModulesGeneratorTests
{
    [Test]
    public async Task NamesEveryReferencedModule()
    {
        var all = await AllAsync(new Module("Orders.Entities"), new Module("Customers.Entities"));

        await Assert.That(all).IsEqualTo("Customers.Entities;Orders.Entities");
    }

    [Test]
    public async Task OrdersIndependentModulesByName()
    {
        // Nothing else to go on, and the compiler's reference order is not a promise. The same
        // references have to produce the same file, or a rebuild is a diff.
        var all = await AllAsync(new Module("Zebra"), new Module("Alpha"), new Module("Mike"));

        await Assert.That(all).IsEqualTo("Alpha;Mike;Zebra");
    }

    [Test]
    public async Task ListsAComposingModuleByNameToo()
    {
        // A set, not an order: the reference assemblies the compiler sees here lack the references
        // only a method body makes, so the order configurations are applied in comes from
        // GetLoadedModules, at runtime.
        var all = await AllAsync(
            new Module("Orders.Entities"),
            new Module("Customers.Entities"),
            new Module("BillingModule", "Orders.Entities", "Customers.Entities"));

        await Assert.That(all).IsEqualTo("BillingModule;Customers.Entities;Orders.Entities");
    }

    [Test]
    public async Task IgnoresReferencesThatAreNotModules()
    {
        // The framework and the base-class assembly are referenced by every host, and a contracts
        // library is a plain class library on purpose.
        var all = await AllAsync(new Module("Orders.Entities"));

        await Assert.That(all).IsEqualTo("Orders.Entities");
    }

    [Test]
    public async Task AHostWithNoModulesGetsAnEmptyList()
    {
        // Emitted anyway: code that says KnownModules.All should not stop compiling because the
        // last module reference was removed.
        var generated = await RunAsync([]);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated).Contains("public const string All = \"\";");
        await Assert.That(generated).Contains("new string[0]");
    }

    [Test]
    public async Task IsGeneratedForATestProject()
    {
        var generated = await RunAsync([new Module("Orders.Entities")], projectKind: "Test");

        await Assert.That(generated).IsNotNull();
    }

    [Test]
    public async Task IsNotGeneratedForAModule()
    {
        // A module has no business knowing the topology it will be deployed in.
        var generated = await RunAsync([new Module("Orders.Entities")], projectKind: "Module");

        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task IsNotGeneratedWhenTurnedOff()
    {
        var generated = await RunAsync([new Module("Orders.Entities")], enabled: "false");

        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task WithoutTheProjectKindFallsBackToWhetherTheProjectRuns()
    {
        var program = await RunAsync([new Module("Orders.Entities")], projectKind: "");
        var library = await RunAsync(
            [new Module("Orders.Entities")],
            projectKind: "",
            hostSource: "class Ordinary { }",
            outputKind: OutputKind.DynamicallyLinkedLibrary);

        await Assert.That(program).IsNotNull();
        await Assert.That(library).IsNull();
    }

    [Test]
    public async Task WritesTheStartupCheckAgainstTheList()
    {
        // RunAsync compiles the result, so this also proves the check compiles where it is written.
        var generated = await RunAsync([new Module("Orders.Entities")]);

        await Assert.That(generated).Contains("public static TBuilder ValidateModules<TBuilder>(this TBuilder builder, bool runtimeTopologyChecks = true)");
        await Assert.That(generated).Contains("\"FusionModules:RuntimeTopologyChecks\"");
        await Assert.That(generated).Contains("global::FusionModules.KnownModules.Names");
    }

    [Test]
    public async Task TheHostsAlternatives_AreWrittenIntoConfigurationByTheStartupCheck()
    {
        // The host's MOD0014 list: modules that implement these are alternatives, one per topology.
        // ValidateModules hands it on so that the runtime's collision message can say so. RunAsync
        // compiles the result, so this also proves both overloads compile with it.
        var generated = await RunAsync(
            [new Module("Orders.Entities")],
            globalOptions: new() { ["build_property.fusion_modules_allowed_service_collisions"] = "Shop.IPaymentGateway, IHandler<A, B>" });

        await Assert.That(generated).Contains("private const string AlternativeServicesKey = \"FusionModules:AlternativeServices\";");
        await Assert.That(generated).Contains("private const string AlternativeServices = \"IHandler<A,B>;Shop.IPaymentGateway\";");
        await Assert.That(generated).Contains("builder.Configuration[AlternativeServicesKey] = AlternativeServices;");
        await Assert.That(generated).Contains("context.Configuration[AlternativeServicesKey] = AlternativeServices;");
    }

    [Test]
    public async Task TheHostsAlternatives_AreReadFromEditorConfigToo()
    {
        // Where MOD0014's page tells a host to write them: an .editorconfig section, which applies to
        // the host's files rather than to the project.
        var generated = await RunAsync(
            [new Module("Orders.Entities")],
            treeOptions: new() { ["fusion_modules_allowed_service_collisions"] = "IPaymentGateway" });

        await Assert.That(generated).Contains("private const string AlternativeServices = \"IPaymentGateway\";");
    }

    [Test]
    public async Task WithoutAlternatives_TheStartupCheckWritesNoKeyForThem()
    {
        // The package declares the property as compiler-visible, so it is always there, empty.
        var generated = await RunAsync(
            [new Module("Orders.Entities")],
            globalOptions: new() { ["build_property.fusion_modules_allowed_service_collisions"] = "" });

        await Assert.That(generated).Contains("public static TBuilder ValidateModules<TBuilder>");
        await Assert.That(generated).DoesNotContain("AlternativeServices");
    }

    [Test]
    public async Task IsNotGeneratedForATestProjectThatSeesTheHostsOwn()
    {
        // A host that grants its tests InternalsVisibleTo hands them its list and check; a second
        // copy of each in the tests would be CS0436 wherever they are used.
        var references = await AnalyzerTest.BaseReferences.Value;
        var framework = await AnalyzerTest.Framework.Value;
        var host = AnalyzerTest.Compile(
            "Host",
            [AnalyzerTest.GeneratedForAHost, """[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]"""],
            references.Add(framework));

        var tests = CSharpCompilation.Create(
            "Tests",
            [CSharpSyntaxTree.ParseText("class Fixture { object All => FusionModules.KnownModules.Names; }")],
            references.Add(framework).Add(host),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create([new KnownModulesGenerator().AsSourceGenerator()], optionsProvider: new Options(new Dictionary<string, string>
            {
                ["build_property.FusionModulesProjectKind"] = "Test",
            }))
            .RunGeneratorsAndUpdateCompilation(tests, out var output, out _);

        await Assert.That(driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources)).IsEmpty();
        await Assert.That(output.GetDiagnostics().Where(d => d.Id == "CS0436")).IsEmpty();
    }
}
