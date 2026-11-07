using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// Builds modules through <see cref="ModuleRecordsGenerator"/> and a host over them, for
/// MOD0014 — the one rule whose two halves run in different builds.
/// </summary>
/// <remarks>
/// The generator writes into the module's assembly and the analyzer reads it out of the host's
/// references, so a test that is worth anything compiles and emits each module for real, as
/// <see cref="GeneratorTest"/> does for <c>KnownModules</c>.
/// </remarks>
internal static class RegistrationTest
{
    /// <summary>The registration methods the rule reads, beside the few in <see cref="AnalyzerTest.FrameworkStubs"/>.</summary>
    public const string DependencyInjectionStubs = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public sealed class ServiceDescriptor
            {
                public static ServiceDescriptor Singleton<TService, TImplementation>()
                    where TImplementation : TService => new ServiceDescriptor();
            }

            public static class MoreServiceCollectionExtensions
            {
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, System.Func<System.IServiceProvider, TService> implementationFactory) => services;
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services, System.Func<System.IServiceProvider, TImplementation> implementationFactory)
                    where TImplementation : TService => services;
                public static IServiceCollection AddSingleton(this IServiceCollection services, System.Type serviceType) => services;
                public static IServiceCollection AddSingleton(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;
                public static IServiceCollection AddSingleton(this IServiceCollection services, System.Type serviceType, object implementationInstance) => services;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection services) => services;
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                    where TImplementation : TService => services;
                public static IServiceCollection AddScoped(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;
                public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)
                    where TImplementation : TService => services;
                public static IServiceCollection AddKeyedSingleton<TService, TImplementation>(this IServiceCollection services, object serviceKey)
                    where TImplementation : TService => services;
                public static IServiceCollection AddHttpClient(this IServiceCollection services, string name) => services;
                public static IServiceCollection AddHttpClient<TClient>(this IServiceCollection services) => services;
                public static IServiceCollection AddHttpClient<TClient, TImplementation>(this IServiceCollection services, System.Action<System.Net.Http.HttpClient> configureClient)
                    where TImplementation : TClient => services;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection.Extensions
        {
            public static class ServiceCollectionDescriptorExtensions
            {
                public static void TryAddSingleton<TService, TImplementation>(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                    where TImplementation : TService { }

                public static void TryAddEnumerable(
                    this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
                    Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor) { }
            }
        }
        """;

    /// <summary>The .NET reference assemblies and the framework stubs with <see cref="DependencyInjectionStubs"/>, compiled once.</summary>
    private static readonly Lazy<Task<ImmutableArray<MetadataReference>>> Framework = new(async () =>
    {
        var references = await AnalyzerTest.BaseReferences.Value;
        return references.Add(AnalyzerTest.Compile(
            "Framework",
            [AnalyzerTest.FrameworkStubs, AnalyzerTest.ModuleBaseStub, DependencyInjectionStubs],
            references));
    });

    /// <summary>A module's registration and usings, put in front of the code under test.</summary>
    private const string ModulePrologue = """
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        using FusionModules;

        [assembly: HostingStartup(typeof({0}Module))]

        """;

    /// <summary>The module class, after the code under test, which may carry assembly attributes of its own.</summary>
    private const string ModuleEpilogue = """

        sealed class {0}Module : ModuleBase { }
        """;

    /// <summary>A module of the test: its assembly name, its code, and whether it is a module at all.</summary>
    public sealed record Module(string Name, string Source)
    {
        /// <summary>Off: the assembly declares no hosting startup — a plain library.</summary>
        public bool Registered { get; init; } = true;

        /// <summary>Assemblies, among the others of the test, that this one references.</summary>
        public string[] References { get; init; } = [];
    }

    /// <summary>
    /// Runs the generator over one module and returns what it generated, or null when it generated
    /// nothing. The module is compiled against <paramref name="contracts"/>, a library of its own.
    /// </summary>
    public static async Task<string?> GenerateAsync(string source, string? contracts = null, string projectKind = "Module", bool registered = true, string assemblyName = "Orders")
    {
        var references = await ReferencesAsync(contracts);
        var (_, generated) = CompileModule(new Module(assemblyName, source) { Registered = registered }, references, projectKind, metadataOnly: false);

        return generated;
    }

    /// <summary>
    /// Builds every module through the generator, then runs <see cref="HostAnalyzer"/> over a host
    /// that references them all, and returns MOD0014's diagnostics.
    /// </summary>
    /// <param name="modules">The modules, in an order where each one's references come before it.</param>
    /// <param name="contracts">A library every module and the host reference, for the shared contracts.</param>
    /// <param name="options">Global analyzer options for the host: <c>.editorconfig</c> keys or <c>build_property.</c> ones.</param>
    /// <param name="metadataOnly">Emit the modules as reference assemblies, as a build with <c>ProduceReferenceAssembly</c> hands them to the host.</param>
    /// <param name="hostKind">The host's project kind, as MSBuild would set it.</param>
    public static async Task<ImmutableArray<Diagnostic>> HostAsync(
        IEnumerable<Module> modules,
        string? contracts = null,
        Dictionary<string, string>? options = null,
        bool metadataOnly = false,
        string hostKind = "Host")
    {
        var references = await ReferencesAsync(contracts);

        var compiled = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var (reference, _) = CompileModule(module, references.AddRange(module.References.Select(name => compiled[name])), "Module", metadataOnly);
            compiled[module.Name] = reference;
        }

        var host = CSharpCompilation.Create(
            "Host",
            [CSharpSyntaxTree.ParseText("class Program { static void Main() { } }")],
            references.AddRange(compiled.Values),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var globals = new Dictionary<string, string>(options ?? [], StringComparer.Ordinal)
        {
            [ModuleFacts.ProjectKindOption] = hostKind,
        };

        var diagnostics = await host
            .WithAnalyzers([new HostAnalyzer()], new AnalyzerOptions([], new GeneratorTest.Options(globals)))
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        return [.. diagnostics.Where(diagnostic => diagnostic.Id == Diagnostics.ModulesRegisterOneServiceDifferentlyId)];
    }

    /// <summary>The framework, and the contracts library when there is one.</summary>
    public static async Task<ImmutableArray<MetadataReference>> ReferencesAsync(string? contracts = null)
    {
        var framework = await Framework.Value;
        return contracts is null ? framework : framework.Add(AnalyzerTest.Compile("Contracts", [contracts], framework));
    }

    /// <summary>
    /// Runs <see cref="ModuleRecordsGenerator"/> over a module, as the package does in a project of
    /// this kind, and emits the result: whole, or as a reference assembly.
    /// </summary>
    /// <remarks>
    /// The generated file is compiled by the consumer, often with warnings as errors: it must not add
    /// even a warning — CS0436 above all, when one module sees another's internals.
    /// </remarks>
    private static (MetadataReference Reference, string? Generated) CompileModule(
        Module module,
        ImmutableArray<MetadataReference> references,
        string projectKind,
        bool metadataOnly)
    {
        var className = ClassName(module.Name);
        var source = module.Registered
            ? ModulePrologue.Replace("{0}", className) + module.Source + ModuleEpilogue.Replace("{0}", className)
            : module.Source;

        var compilation = CSharpCompilation.Create(
            module.Name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create([new ModuleRecordsGenerator().AsSourceGenerator()], optionsProvider: new GeneratorTest.Options(new() { [ModuleFacts.ProjectKindOption] = projectKind }))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        if (!generatorDiagnostics.IsEmpty)
        {
            throw new InvalidOperationException($"The generator reported: {string.Join("; ", generatorDiagnostics)}");
        }

        var problems = output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{module.Name} did not compile cleanly: {string.Join("; ", problems)}");
        }

        using var stream = new MemoryStream();
        var result = output.Emit(stream, options: new EmitOptions(metadataOnly: metadataOnly, includePrivateMembers: !metadataOnly));
        if (!result.Success)
        {
            throw new InvalidOperationException($"{module.Name} did not compile: {string.Join("; ", result.Diagnostics)}");
        }

        var generated = driver.GetRunResult().Results.SelectMany(run => run.GeneratedSources).SingleOrDefault();
        return (MetadataReference.CreateFromImage(stream.ToArray()), generated.SourceText?.ToString());
    }

    /// <summary>A module class name per assembly: a module that sees another's internals must not meet a namesake.</summary>
    private static string ClassName(string assemblyName) => new([.. assemblyName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
}
