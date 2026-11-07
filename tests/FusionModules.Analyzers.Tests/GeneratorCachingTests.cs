using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using static FusionModules.Analyzers.Tests.GeneratorTest;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// Both generators are incremental in fact, not only in name: run again over an unchanged
/// compilation, over one where an unrelated file changed, or over one where the project's own code
/// changed without changing what it registers, every tracked step produces what it produced before
/// and is served from the cache, and no step holds on to a compilation, a symbol or a syntax node.
/// </summary>
/// <remarks>
/// A generator runs on every keystroke in the IDE, in every project that references the package. A
/// step whose value compares by reference — an <c>ImmutableArray</c>, a symbol — looks changed every
/// time, and everything after it runs again; a step that keeps a symbol keeps its whole compilation
/// alive. Both are invisible until an editor slows down, so they are checked here, for a project the
/// generator writes into and for one it leaves alone, which is most of them.
/// </remarks>
public class GeneratorCachingTests
{
    private const string OtherFileName = "Other.cs";

    private const string OtherFile = """
        namespace Elsewhere
        {
            internal static class Unrelated { public static int Value => 1; }
        }
        """;

    private const string OrdersModule = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        [assembly: HostingStartup(typeof(Orders.OrdersModule))]

        namespace Orders
        {
            public interface IChecker { }

            internal sealed class Checker : IChecker { }

            internal sealed class OrdersModule : ModuleBase
            {
                protected override void ConfigureServices(IHostApplicationBuilder builder) =>
                    builder.Services.AddSingleton<IChecker, Checker>();
            }
        }
        """;

    private static readonly string[] ModuleRecordsSteps =
    [
        TrackingNames.ProjectKind,
        TrackingNames.Module,
        TrackingNames.RegistrationCandidates,
        TrackingNames.Registrations,
    ];

    private static readonly string[] KnownModulesSteps =
    [
        TrackingNames.KnownModulesSettings,
        TrackingNames.KnownModules,
    ];

    [Test]
    public async Task ModuleRecords_InAModule_AreCached() =>
        await AssertCachedAsync(
            new ModuleRecordsGenerator(),
            await ModuleCompilationAsync(),
            Options("Module"),
            expectOutput: true,
            ModuleRecordsSteps);

    [Test]
    public async Task ModuleRecords_InAHost_WriteNothing_AndAreCached()
    {
        // A registration in the host too, so that every step has something to look at and decline.
        var (host, _) = await CreateHostAsync(
            [new GeneratorTest.Module("Orders.Entities")],
            "Host",
            null,
            """
            using Microsoft.Extensions.DependencyInjection;

            class Program
            {
                static void Main()
                {
                    IServiceCollection services = null;
                    services.AddSingleton<Program>();
                }
            }
            """,
            OutputKind.ConsoleApplication);

        await AssertCachedAsync(new ModuleRecordsGenerator(), host, Options("Host"), expectOutput: false, ModuleRecordsSteps);
    }

    [Test]
    public async Task KnownModules_InAHost_AreCached()
    {
        var (host, options) = await CreateHostAsync(
            [new GeneratorTest.Module("Orders.Entities"), new GeneratorTest.Module("Customers.Entities"), new GeneratorTest.Module("BillingModule", "Orders.Entities", "Customers.Entities")],
            "Host",
            null,
            "class Program { static void Main() { } }",
            OutputKind.ConsoleApplication);

        await AssertCachedAsync(new KnownModulesGenerator(), host, options, expectOutput: true, KnownModulesSteps);
    }

    [Test]
    public async Task KnownModules_InAHostWithAlternatives_AreCached()
    {
        // The alternatives go into the generated file, read from the project and from .editorconfig:
        // both have to compare by value for a keystroke to change nothing.
        var (host, options) = await CreateHostAsync(
            [new GeneratorTest.Module("Orders.Entities"), new GeneratorTest.Module("Customers.Entities")],
            "Host",
            null,
            "class Program { static void Main() { } }",
            OutputKind.ConsoleApplication,
            globalOptions: new() { ["build_property.fusion_modules_allowed_service_collisions"] = "Shop.IPaymentGateway" },
            treeOptions: new() { ["fusion_modules_allowed_service_collisions"] = "IClock" });

        await AssertCachedAsync(new KnownModulesGenerator(), host, options, expectOutput: true, KnownModulesSteps);
    }

    [Test]
    public async Task KnownModules_InAModule_WriteNothing_AndAreCached() =>
        await AssertCachedAsync(
            new KnownModulesGenerator(),
            await ModuleCompilationAsync(),
            Options("Module"),
            expectOutput: false,
            KnownModulesSteps);

    private static async Task<CSharpCompilation> ModuleCompilationAsync() =>
        CSharpCompilation.Create(
            "Orders",
            [CSharpSyntaxTree.ParseText(OrdersModule, path: "Module.cs")],
            await RegistrationTest.ReferencesAsync(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static AnalyzerConfigOptionsProvider Options(string projectKind) =>
        new GeneratorTest.Options(new() { [ModuleFacts.ProjectKindOption] = projectKind });

    /// <summary>
    /// Runs the generator, then again over a clone of the compilation, over one where an unrelated
    /// file gained a comment, and over one where the project's own first file did — in a module, the
    /// file that registers its services; each tracked step must produce what it produced before, and
    /// the generated files must stay what they were.
    /// </summary>
    private static async Task AssertCachedAsync(
        IIncrementalGenerator generator,
        CSharpCompilation compilation,
        AnalyzerConfigOptionsProvider options,
        bool expectOutput,
        params string[] trackingNames)
    {
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(OtherFile, path: OtherFileName));

        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        await Assert.That(errors).IsEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            optionsProvider: options,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var firstResult = driver.GetRunResult();
        var first = Steps(firstResult, trackingNames);
        var generated = Generated(firstResult);
        await Assert.That(generated.Count > 0).IsEqualTo(expectOutput)
            .Because(expectOutput ? "the generator should write here" : "the generator should leave this project alone");

        foreach (var name in trackingNames)
        {
            await Assert.That(first.ContainsKey(name)).IsTrue().Because($"{name} should be a tracked step");
            await AssertNoCompilationObjectsAsync(first[name], name);
        }

        var other = compilation.SyntaxTrees.Single(tree => tree.FilePath == OtherFileName);
        var own = compilation.SyntaxTrees.First();

        foreach (var (label, next) in new[] { ("clone", compilation.Clone()), ("unrelated edit", Edit(compilation, other)), ("edit in the project's own file", Edit(compilation, own)) })
        {
            var result = driver.RunGenerators(next).GetRunResult();
            var second = Steps(result, trackingNames);
            await Assert.That(second.Count).IsEqualTo(first.Count);

            foreach (var name in trackingNames)
            {
                await Assert.That(second.ContainsKey(name)).IsTrue();
                await AssertSameAsync(first[name], second[name], $"{name} after {label}");
            }

            await Assert.That(Generated(result)).IsEquivalentTo(generated, TUnit.Assertions.Enums.CollectionOrdering.Matching)
                .Because($"the generated files should not change after {label}");
        }
    }

    private static CSharpCompilation Edit(CSharpCompilation compilation, SyntaxTree tree) =>
        compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(tree.GetText().Replace(0, 0, "// an edit\n")));

    private static Dictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> Steps(GeneratorDriverRunResult result, string[] trackingNames) =>
        result.Results[0].TrackedSteps
            .Where(step => trackingNames.Contains(step.Key))
            .ToDictionary(step => step.Key, step => step.Value);

    private static List<string> Generated(GeneratorDriverRunResult result) =>
        [.. result.Results[0].GeneratedSources.Select(source => source.HintName + "\n" + source.SourceText)];

    private static async Task AssertSameAsync(ImmutableArray<IncrementalGeneratorRunStep> first, ImmutableArray<IncrementalGeneratorRunStep> second, string step)
    {
        // A syntax step runs once per candidate node, and its transform runs again on every edit — the
        // semantic model changed — so its outputs may be Unchanged rather than Cached; what matters is
        // that they are equal, and that nothing after them runs.
        var before = first.SelectMany(run => run.Outputs).Select(output => output.Value).ToList();
        var after = second.SelectMany(run => run.Outputs).Select(output => output.Value).ToList();
        await Assert.That(after).IsEquivalentTo(before, TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because($"{step} should produce what it produced before");

        // Unchanged: the input changed and the output did not. Cached: the input did not change either.
        var reasons = second.SelectMany(run => run.Outputs).Select(output => output.Reason).ToList();
        await Assert.That(reasons.All(reason => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)).IsTrue()
            .Because($"{step} should come from the cache, not be {string.Join(", ", reasons.Distinct())}");
    }

    /// <summary>Nothing a step produces may hold a compilation, a symbol or a syntax node, however deep.</summary>
    private static async Task AssertNoCompilationObjectsAsync(ImmutableArray<IncrementalGeneratorRunStep> runs, string step)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<string>();

        void Visit(object? node)
        {
            if (node is null || !visited.Add(node))
            {
                return;
            }

            if (node is Compilation or ISymbol or SyntaxNode or SemanticModel)
            {
                found.Add(node.GetType().Name);
                return;
            }

            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || node is string)
            {
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
                (bool)type.GetProperty(nameof(ImmutableArray<int>.IsDefault))!.GetValue(node)!)
            {
                return;
            }

            if (node is IEnumerable sequence)
            {
                foreach (var element in sequence)
                {
                    Visit(element);
                }

                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Visit(field.GetValue(node));
            }
        }

        foreach (var output in runs.SelectMany(run => run.Outputs))
        {
            Visit(output.Value);
        }

        await Assert.That(found).IsEmpty().Because($"{step} should hold no compilation objects");
    }
}
