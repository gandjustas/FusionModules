using System.Reflection;

namespace FusionModules.Tests;

/// <summary>
/// Every name the runtime reads — a configuration key, an attribute it recognises — is in
/// docs/runtime.md.
/// </summary>
/// <remarks>
/// The keys and the marker attribute are the package's runtime contract, and they are declared as
/// constants in the code. A new one used to reach users through a startup failure or the skill
/// before it reached any page; this makes adding one without documenting it a failing test, the
/// way the analyzer catalogue tests do for rules.
/// </remarks>
public class RuntimeDocumentationTests
{
    private static string RepositoryRoot { get; } = typeof(RuntimeDocumentationTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "RepositoryRoot")
        .Value!;

    public static IEnumerable<Func<string>> RuntimeNames() =>
        typeof(ModuleBase).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(name => new Func<string>(() => name));

    [Test]
    [MethodDataSource(nameof(RuntimeNames))]
    public async Task IsDocumented(string name)
    {
        var page = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot, "docs", "runtime.md"));

        await Assert.That(page).Contains(name);
    }

    [Test]
    public async Task TheKnownNames_AreAmongThemSoTheTestIsNotVacuous()
    {
        var names = RuntimeNames().Select(name => name()).ToList();

        await Assert.That(names).Contains("FusionModules:AllowedServiceCollisions");
        await Assert.That(names).Contains("MultipleImplementationsAttribute");
    }
}
