using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// Runs a generator over a host compilation that references real module assemblies.
/// </summary>
/// <remarks>
/// Real assemblies rather than sources in one compilation, because that is the question the
/// generator asks: a module is a thing this project <em>references</em>, and the attribute it is
/// recognised by has to be read out of metadata. Compiling the modules for real is also what makes
/// the dependency edge between them exist at all.
/// </remarks>
internal static class GeneratorTest
{
    /// <summary>A module assembly: its name, and the modules it references.</summary>
    public sealed record Module(string Name, params string[] References);

    /// <summary>
    /// Generates against a host that references <paramref name="modules"/>, and returns the
    /// generated file, or null when the generator produced nothing.
    /// </summary>
    public static async Task<string?> RunAsync(
        IEnumerable<Module> modules,
        string projectKind = "Host",
        string? enabled = null,
        string hostSource = "class Program { static void Main() { } }",
        OutputKind outputKind = OutputKind.ConsoleApplication)
    {
        var (host, options) = await CreateHostAsync(modules, projectKind, enabled, hostSource, outputKind);

        var driver = CSharpGeneratorDriver
            .Create([new KnownModulesGenerator().AsSourceGenerator()], optionsProvider: options)
            .RunGeneratorsAndUpdateCompilation(host, out var output, out var diagnostics);

        await Assert.That(diagnostics).IsEmpty();

        // The generated file is compiled by the consumer, so "it compiles" is part of the contract.
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        await Assert.That(errors).IsEmpty();

        var generated = driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources).ToList();

        return generated.Count == 0 ? null : generated.Single().SourceText.ToString();
    }

    /// <summary>
    /// Generates, edits the host's code without touching its references, generates again, and
    /// returns why the generator's module step produced what it did the second time.
    /// </summary>
    public static async Task<IReadOnlyList<IncrementalStepRunReason>> RunAfterAnEditAsync(params Module[] modules)
    {
        var (host, options) = await CreateHostAsync(modules, "Host", null, "class Program { static void Main() { } }", OutputKind.ConsoleApplication);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new KnownModulesGenerator().AsSourceGenerator()],
            optionsProvider: options,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(host);
        driver = driver.RunGenerators(host.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Edited { int Value => 42; }")));

        return driver.GetRunResult().Results
            .Single()
            .TrackedSteps[KnownModulesGenerator.ModulesStep]
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToList();
    }

    private static async Task<(CSharpCompilation Host, AnalyzerConfigOptionsProvider Options)> CreateHostAsync(
        IEnumerable<Module> modules,
        string projectKind,
        string? enabled,
        string hostSource,
        OutputKind outputKind)
    {
        var references = await AnalyzerTest.BaseReferencesAsync();
        var framework = await AnalyzerTest.FrameworkAsync();

        var compiled = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            var index = compiled.Count;
            indexes[module.Name] = index;

            // A module that names another one has to use a type from it. Otherwise the compiler
            // emits no reference to that assembly at all and there is no edge to sort on — which
            // is the same rule the package states about deployment: a reference exists because
            // something is used.
            var uses = string.Join(
                Environment.NewLine,
                module.References.Select(name => $"    static readonly System.Type Use{indexes[name]} = typeof(Marker{indexes[name]});"));

            var source = $$"""
                using Microsoft.AspNetCore.Hosting;
                using FusionModules;

                [assembly: HostingStartup(typeof(Module{{index}}))]

                public class Marker{{index}} { }

                sealed class Module{{index}} : ModuleBase
                {
                {{uses}}
                }
                """;

            compiled[module.Name] = AnalyzerTest.Compile(
                module.Name,
                [source],
                references.Add(framework).AddRange(module.References.Select(name => compiled[name])));
        }

        var host = CSharpCompilation.Create(
            "Host",
            [CSharpSyntaxTree.ParseText(hostSource)],
            references.Add(framework).AddRange(compiled.Values),
            new CSharpCompilationOptions(outputKind));

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectKind.Length > 0)
        {
            options["build_property.FusionModulesProjectKind"] = projectKind;
        }

        if (enabled is not null)
        {
            options["build_property.FusionModulesKnownModules"] = enabled;
        }

        return (host, new Options(options));
    }

    /// <summary>The value of the generated <c>All</c> constant.</summary>
    public static async Task<string?> AllAsync(params Module[] modules)
    {
        var generated = await RunAsync(modules);

        return generated is null ? null : Between(generated, "public const string All = \"", "\";");
    }

    private static string Between(string text, string prefix, string suffix)
    {
        var start = text.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = text.IndexOf(suffix, start, StringComparison.Ordinal);

        return text[start..end];
    }

    private sealed class Options(Dictionary<string, string> globals) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Config(globals);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Config([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Config([]);

        private sealed class Config(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }
}
