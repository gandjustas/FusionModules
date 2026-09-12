using System.Globalization;
using System.Reflection;
using System.Resources;
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
    public static TheoryData<DiagnosticDescriptor> AllDescriptors()
    {
        var data = new TheoryData<DiagnosticDescriptor>();
        foreach (var descriptor in Analyzers.SelectMany(a => a.SupportedDiagnostics).DistinctBy(d => d.Id))
        {
            data.Add(descriptor);
        }

        return data;
    }

    private static DiagnosticAnalyzer[] Analyzers => [new ModuleAnalyzer(), new HostAnalyzer()];

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void MessageFormat_HasNoUnfilledPlaceholders(DiagnosticDescriptor descriptor)
    {
        // MOD0004 shipped with a three-placeholder message and two arguments, so it rendered as
        // "{2}" to whoever hit it. A format string is only as good as its callers, but at least
        // make sure the placeholders are contiguous from zero.
        var placeholders = Regex.Matches(descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture), @"\{(\d+)\}")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(Enumerable.Range(0, placeholders.Length), placeholders);
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Descriptor_IsFullyPopulated(DiagnosticDescriptor descriptor)
    {
        Assert.StartsWith("MOD", descriptor.Id, StringComparison.Ordinal);
        Assert.NotEmpty(descriptor.Title.ToString(CultureInfo.InvariantCulture));
        Assert.NotEmpty(descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture));
        Assert.NotEmpty(descriptor.Description.ToString(CultureInfo.InvariantCulture));
        Assert.Contains(descriptor.Id, descriptor.HelpLinkUri, StringComparison.Ordinal);
        Assert.True(descriptor.IsEnabledByDefault);
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Descriptor_IsTranslated(DiagnosticDescriptor descriptor)
    {
        // The Russian satellite is easy to forget when adding a rule, and a missing entry falls
        // back to English silently.
        var russian = new ResourceManager(typeof(Resources))
            .GetResourceSet(new CultureInfo("ru"), createIfNotExists: true, tryParents: false);

        Assert.NotNull(russian);
        foreach (var suffix in new[] { "Title", "Message", "Description" })
        {
            Assert.NotNull(russian.GetString($"{descriptor.Id}_{suffix}"));
        }
    }

    [Fact]
    public void Descriptors_AreUnique()
    {
        var ids = Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).ToArray();

        // The same descriptor may be supported by more than one analyzer, but two different
        // descriptors must never share an id.
        var byId = Analyzers.SelectMany(a => a.SupportedDiagnostics).GroupBy(d => d.Id);
        Assert.All(byId, group => Assert.Single(group.DistinctBy(d => d.Title.ToString(CultureInfo.InvariantCulture))));
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void EveryRule_IsListedInTheReleaseFile()
    {
        // Release tracking is what turns a new rule into a documented, reviewable change.
        var assemblyDirectory = Path.GetDirectoryName(typeof(ModuleAnalyzer).Assembly.Location)!;
        var repositoryRoot = FindRepositoryRoot(assemblyDirectory);
        var released = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Modulith.Analyzers", "AnalyzerReleases.Unshipped.md")) +
            File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Modulith.Analyzers", "AnalyzerReleases.Shipped.md"));

        Assert.All(
            Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).Distinct(),
            id => Assert.Contains(id, released, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Modulith.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No repository root above '{start}'.");
    }
}
