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
    public async Task AnEditThatLeavesTheReferencesAloneRegeneratesNothing()
    {
        // In the IDE the generator runs on every keystroke. The module list depends on the
        // references only, so a keystroke must end at this step with the same value as before,
        // and the file must not be written again.
        var reasons = await RunAfterAnEditAsync(new Module("Orders.Entities"), new Module("Customers.Entities"));

        await Assert.That(reasons).IsNotEmpty();
        await Assert.That(reasons.All(reason => reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached)).IsTrue();
    }

    [Test]
    public async Task WritesTheStartupCheckAgainstTheList()
    {
        // RunAsync compiles the result, so this also proves the check compiles where it is written.
        var generated = await RunAsync([new Module("Orders.Entities")]);

        await Assert.That(generated).Contains("public static TBuilder ValidateModules<TBuilder>(this TBuilder builder)");
        await Assert.That(generated).Contains("global::FusionModules.KnownModules.Names");
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
