using System.Reflection;

namespace FusionModules.Tool.Tests;

/// <summary>
/// A directory of files written for one test, under the system temp directory — outside the
/// repository, so that its Directory.Build.props and central package versions do not apply to the
/// fixture's projects.
/// </summary>
internal sealed class Fixture : IDisposable
{
    public Fixture(params (string Path, string Content)[] files)
    {
        Root = Path.Combine(Path.GetTempPath(), "fusion-modules-tool-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        foreach (var (path, content) in files)
        {
            Write(path, content);
        }
    }

    public string Root { get; }

    public string this[string relative] => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var full = this[relative];
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // An MSBuild node may still hold a file; the temp directory is cleaned eventually.
        }
    }

    public static string RepositoryRoot { get; } = typeof(Fixture).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "RepositoryRoot")
        .Value!;

    public static string Project(string sdk = "Microsoft.NET.Sdk", string body = "") => $"""
        <Project Sdk="{sdk}">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        {body}
        </Project>
        """;

    public static string Executable(string sdk = "Microsoft.NET.Sdk.Web", string body = "") => Project(sdk, $"""
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        {body}
        """);

    public static string Solution(params string[] projects) =>
        "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n";

    /// <summary>
    /// The tool with --static: evaluation and deployment files only, no restore, build or probe.
    /// Any error in the document fails the test first, by name: a project MSBuild could not evaluate
    /// would otherwise only show as a project of the wrong kind further down.
    /// </summary>
    public static async Task<InventoryDocument> StaticInventory(string path, params string[] extra)
    {
        var document = await StaticInventoryWithErrors(path, extra);
        if (document.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                "The inventory reported errors:\n" + string.Join("\n", document.Errors.Select(e => $"{e.Stage} {e.Subject}: {e.Message}")));
        }

        return document;
    }

    /// <summary>The same, for a test of the errors themselves.</summary>
    public static Task<InventoryDocument> StaticInventoryWithErrors(string path, params string[] extra)
    {
        var options = Program.Parse([path, "--static", "-q", .. extra]);
        return new Inventory(options, _ => { }).RunAsync();
    }
}
