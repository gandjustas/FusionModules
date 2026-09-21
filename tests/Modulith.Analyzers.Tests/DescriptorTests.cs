using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Modulith.Analyzers.Tests;

/// <summary>
/// Checks over the rule catalogue itself. Cheap, and they close off mistakes that are otherwise
/// found only by a user hitting the rule — the demo analyzers shipped two of them.
/// </summary>
public class DescriptorTests
{
    private static string RepositoryRoot { get; } = typeof(DescriptorTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "RepositoryRoot")
        .Value!;

    private static DiagnosticAnalyzer[] Analyzers =>
        [new ModuleAnalyzer(), new HostAnalyzer(), new ModuleUsageAnalyzer()];

    public static IEnumerable<Func<DiagnosticDescriptor>> AllDescriptors() =>
        Analyzers
            .SelectMany(analyzer => analyzer.SupportedDiagnostics)
            .DistinctBy(descriptor => descriptor.Id)
            .Select(descriptor => new Func<DiagnosticDescriptor>(() => descriptor));

    [Test]
    [MethodDataSource(nameof(AllDescriptors))]
    public async Task MessageFormat_HasNoUnfilledPlaceholders(DiagnosticDescriptor descriptor)
    {
        // MOD0004 shipped with a three-placeholder message and two arguments, so it rendered as
        // "{2}" to whoever hit it. A format string is only as good as its callers, but at least
        // make sure the placeholders are contiguous from zero.
        var placeholders = Regex.Matches(descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture), @"\{(\d+)\}")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

        await Assert.That(placeholders).IsEquivalentTo(Enumerable.Range(0, placeholders.Length).ToArray());
    }

    [Test]
    [MethodDataSource(nameof(AllDescriptors))]
    public async Task Descriptor_IsFullyPopulated(DiagnosticDescriptor descriptor)
    {
        await Assert.That(descriptor.Id).StartsWith("MOD");
        await Assert.That(descriptor.Title.ToString(CultureInfo.InvariantCulture)).IsNotEmpty();
        await Assert.That(descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture)).IsNotEmpty();
        await Assert.That(descriptor.Description.ToString(CultureInfo.InvariantCulture)).IsNotEmpty();
        await Assert.That(descriptor.HelpLinkUri).Contains(descriptor.Id);
        await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(AllDescriptors))]
    public async Task Descriptor_HasItsDocumentationPage(DiagnosticDescriptor descriptor)
    {
        // Descriptor_IsFullyPopulated only checks that the help link contains the id. A rule
        // shipped without its page has a link that resolves to a 404 in the IDE, and nothing
        // anywhere goes red — which is how MOD0009's page came to be written but untracked.
        var page = Path.Combine(RepositoryRoot, "docs", "rules", $"{descriptor.Id}.md");

        await Assert.That(File.Exists(page)).IsTrue();
        await Assert.That(descriptor.HelpLinkUri).EndsWith($"docs/rules/{descriptor.Id}.md");
    }

    [Test]
    [MethodDataSource(nameof(AllDescriptors))]
    public async Task Descriptor_IsListedWhereRulesAreListed(DiagnosticDescriptor descriptor)
    {
        // Two hand-maintained lists. The release file is what the Roslyn analyzer RS2000 reads,
        // and the README table is what a reader reads; both drift the same silent way.
        var releases = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot, "src", "Modulith.Analyzers", "AnalyzerReleases.Unshipped.md"));
        var readme = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot, "README.md"));

        await Assert.That(releases).Contains(descriptor.Id);
        await Assert.That(readme).Contains($"docs/rules/{descriptor.Id}.md");
    }

    [Test]
    public async Task EveryDocumentedRuleStillExists()
    {
        // The other direction: a rule that is removed leaves its page behind, and the page then
        // documents behaviour nothing implements.
        var documented = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "docs", "rules"), "MOD*.md")
            .Select(page => Path.GetFileNameWithoutExtension(page)!)
            .Order();

        var implemented = AllDescriptors().Select(descriptor => descriptor().Id).Order();

        await Assert.That(documented).IsEquivalentTo(implemented);
    }

    [Test]
    public async Task OneIdMeansOneRule()
    {
        // The same descriptor may be supported by more than one analyzer, but two different
        // descriptors must never share an id.
        var byId = Analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).GroupBy(descriptor => descriptor.Id);

        await Assert.That(byId).IsNotEmpty();
        foreach (var group in byId)
        {
            await Assert.That(group.DistinctBy(d => d.Title.ToString(CultureInfo.InvariantCulture))).HasSingleItem();
        }
    }
}
