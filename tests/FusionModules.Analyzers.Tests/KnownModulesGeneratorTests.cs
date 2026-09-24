using Microsoft.CodeAnalysis;
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
    public async Task PutsAComposingModuleAfterTheModulesItJoins()
    {
        // BillingModule sorts first by name and must not come first: it joins the two entity
        // modules, so its entity configurations have to be applied after theirs.
        var all = await AllAsync(
            new Module("Orders.Entities"),
            new Module("Customers.Entities"),
            new Module("BillingModule", "Orders.Entities", "Customers.Entities"));

        await Assert.That(all).IsEqualTo("Customers.Entities;Orders.Entities;BillingModule");
    }

    [Test]
    public async Task OrdersAChainOfDependencies()
    {
        var all = await AllAsync(
            new Module("A"),
            new Module("B", "A"),
            new Module("C", "B"));

        await Assert.That(all).IsEqualTo("A;B;C");
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
        await Assert.That(generated!).Contains("public const string All = \"\";");
        await Assert.That(generated!).Contains("new string[0]");
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
}
