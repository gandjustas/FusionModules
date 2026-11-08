using System.Text.Json;
using System.Xml.Linq;

namespace FusionModules.Tool;

internal sealed class InventoryInputException(string message) : Exception(message);

internal sealed record ResolvedInput(string Root, string? Solution, string? Project);

internal static class InputResolver
{
    private static readonly string[] SolutionExtensions = [".slnx", ".sln", ".slnf"];
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static bool IsProjectFile(string path) => ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static ResolvedInput Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            var extension = Path.GetExtension(full);
            if (SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return new ResolvedInput(Path.GetDirectoryName(full)!, full, null);
            }

            if (IsProjectFile(full))
            {
                return new ResolvedInput(Path.GetDirectoryName(full)!, null, full);
            }

            throw new InventoryInputException($"'{path}' is neither a solution nor a project.");
        }

        if (!Directory.Exists(full))
        {
            throw new InventoryInputException($"'{path}' does not exist.");
        }

        var solutions = Solutions(Directory.EnumerateFiles(full));
        if (solutions.Count == 1)
        {
            return new ResolvedInput(full, solutions[0], null);
        }

        if (solutions.Count > 1)
        {
            throw new InventoryInputException($"'{path}' holds several solutions ({string.Join(", ", solutions.Select(Path.GetFileName))}): name one.");
        }

        var projects = Directory.EnumerateFiles(full).Where(IsProjectFile).ToList();
        if (projects.Count == 1)
        {
            return new ResolvedInput(full, null, projects[0]);
        }

        if (projects.Count > 1)
        {
            throw new InventoryInputException($"'{path}' holds several projects and no solution: name one.");
        }

        // A repository root whose solution lives under src/: the one solution below, if there is one.
        var below = Solutions(FilesBelow(full, SearchDepth));
        return below.Count switch
        {
            1 => new ResolvedInput(Path.GetDirectoryName(below[0])!, below[0], null),
            0 => throw new InventoryInputException($"'{path}' holds no solution and no project, nor does any directory below it (to {SearchDepth} levels)."),
            _ => throw new InventoryInputException(
                $"'{path}' holds no solution, and the directories below it hold several: {string.Join(", ", below.Select(s => Path.GetRelativePath(full, s).Replace('\\', '/')))}. Name one."),
        };
    }

    private const int SearchDepth = 3;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "artifacts" };

    /// <summary>The .sln and .slnx files among these; of a .sln and a .slnx of one name, the .slnx.</summary>
    private static List<string> Solutions(IEnumerable<string> files)
    {
        var solutions = files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".sln" or ".slnx").ToList();

        // A repository in the middle of moving to .slnx often has both, of one name.
        return [.. solutions
            .GroupBy(s => Path.Combine(Path.GetDirectoryName(s)!, Path.GetFileNameWithoutExtension(s)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(s => s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) ?? g.First())
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The files in the directories below this one, to a depth, leaving out build output, packages and dot-folders.</summary>
    private static IEnumerable<string> FilesBelow(string directory, int depth)
    {
        if (depth == 0)
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedDirectories.Contains(name))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var file in FilesBelow(child, depth - 1))
            {
                yield return file;
            }
        }
    }
}

internal static class SolutionReader
{
    /// <summary>
    /// The solution's projects: a .slnf read as the JSON it is; a .sln or .slnx by `dotnet sln list`,
    /// with the .slnx's XML as the fallback.
    /// </summary>
    public static async Task<List<string>> ProjectsAsync(string solution, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(solution).Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return FilterProjects(solution);
        }

        var directory = Path.GetDirectoryName(solution)!;
        var result = await ProcessRunner.RunAsync("dotnet", ["sln", solution, "list"], directory, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        if (result.Succeeded)
        {
            var projects = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(InputResolver.IsProjectFile)
                .Select(p => Path.GetFullPath(Path.Combine(directory, p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (projects.Count > 0)
            {
                return projects;
            }
        }

        if (Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return [.. XDocument.Load(solution).Descendants("Project")
                .Select(p => (string?)p.Attribute("Path"))
                .Where(p => p is not null && InputResolver.IsProjectFile(p))
                .Select(p => Path.GetFullPath(Path.Combine(directory, p!)))];
        }

        throw new InventoryInputException($"Could not list the projects of {solution}: {ProcessRunner.Tail(result.Output, 5)}");
    }

    /// <summary>
    /// A solution filter: <c>{ "solution": { "path": "../Shop.sln", "projects": ["src\\Api\\Api.csproj"] } }</c>.
    /// The solution's path is relative to the filter, the projects' to the solution — as Visual Studio
    /// and MSBuild read it; a project found only relative to the filter is taken from there. Either
    /// separator. (`dotnet sln list` on a filter with forward slashes finds nothing on SDK 10.0.403.)
    /// </summary>
    internal static List<string> FilterProjects(string filter)
    {
        var filterDirectory = Path.GetDirectoryName(Path.GetFullPath(filter))!;
        string? solutionPath;
        List<string> projects;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filter), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var solution = document.RootElement.GetProperty("solution");
            solutionPath = solution.TryGetProperty("path", out var path) ? path.GetString() : null;
            projects = solution.TryGetProperty("projects", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(p => p.GetString()).OfType<string>()]
                : [];
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InventoryInputException($"Could not read the solution filter {filter}: {exception.Message}");
        }

        static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        var solutionDirectory = solutionPath is null
            ? filterDirectory
            : Path.GetDirectoryName(Path.GetFullPath(Path.Combine(filterDirectory, Normalize(solutionPath))))!;
        return [.. projects
            .Where(InputResolver.IsProjectFile)
            .Select(p =>
            {
                var fromSolution = Path.GetFullPath(Path.Combine(solutionDirectory, Normalize(p)));
                var fromFilter = Path.GetFullPath(Path.Combine(filterDirectory, Normalize(p)));
                return File.Exists(fromSolution) || !File.Exists(fromFilter) ? fromSolution : fromFilter;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

internal static class ProjectEvaluator
{
    private static readonly string[] Properties =
    [
        "OutputType", "TargetFramework", "TargetFrameworks", "AssemblyName", "IsTestProject", "IsAspireHost",
        "UsingMicrosoftNETSdkWeb", "UsingMicrosoftNETSdkWorker", "UsingMicrosoftNETSdkRazor", "UsingMicrosoftNETSdkBlazorWebAssembly",
        "UseMaui", "UseWPF", "UseWindowsForms", "UserSecretsId", "ContainerRepository", "EnableSdkContainerSupport",
        "TargetPath", "ProjectDepsFilePath", "ProjectRuntimeConfigFilePath", "ProjectAssetsFile",
    ];

    private static readonly string[] Items = ["ProjectReference", "PackageReference", "PackageVersion", "FrameworkReference"];

    /// <summary>Package id prefixes that make a project a test project whatever it says about itself.</summary>
    private static readonly string[] TestPackagePrefixes =
    [
        "Microsoft.NET.Test.Sdk", "TUnit", "xunit", "NUnit", "MSTest", "Microsoft.Testing.Platform",
    ];

    private static readonly string[] ReportedProperties = ["UserSecretsId", "ContainerRepository", "EnableSdkContainerSupport", "UsingMicrosoftNETSdkRazor"];

    /// <summary>
    /// `dotnet msbuild -getProperty -getItem`: evaluation only, no targets run, so no build and no
    /// restore — and the same answer the build would get, conditions, imports and central package
    /// management included. Multi-targeted projects are evaluated twice, the second time for one
    /// target framework, because item conditions on the framework only apply there.
    /// </summary>
    public static async Task<ProjectInfo> EvaluateAsync(string projectPath, IReadOnlyList<string> globalProperties, string? framework, CancellationToken cancellationToken)
    {
        var project = new ProjectInfo
        {
            FullPath = projectPath,
            Name = Path.GetFileNameWithoutExtension(projectPath),
            Sdk = ReadSdk(projectPath),
        };

        try
        {
            var outer = await EvaluateOnceAsync(projectPath, globalProperties, null, cancellationToken);
            var properties = outer.Properties;
            var frameworks = Split(properties.GetValueOrDefault("TargetFrameworks"));
            if (frameworks.Count == 0 && properties.GetValueOrDefault("TargetFramework") is { Length: > 0 } single)
            {
                frameworks = [single];
            }

            project.TargetFrameworks = frameworks;
            var items = outer.Items;
            if (string.IsNullOrEmpty(properties.GetValueOrDefault("TargetFramework")) && frameworks.Count > 0)
            {
                var chosen = Frameworks.Choose(frameworks, framework)!;
                var inner = await EvaluateOnceAsync(projectPath, globalProperties, chosen, cancellationToken);
                properties = inner.Properties;
                items = inner.Items;
            }

            Apply(project, properties, items);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            project.Error = exception.Message;
        }

        return project;
    }

    private sealed record Evaluation(Dictionary<string, string> Properties, Dictionary<string, List<Dictionary<string, string>>> Items);

    /// <summary>
    /// One evaluation, tried a second time when the first fails: of many `dotnet msbuild` processes
    /// started at once, one now and then fails or prints something that is not its JSON, and does
    /// not the second time. A project that really cannot be evaluated fails the same way twice,
    /// and its error is recorded.
    /// </summary>
    private static async Task<Evaluation> EvaluateOnceAsync(string projectPath, IReadOnlyList<string> globalProperties, string? framework, CancellationToken cancellationToken)
    {
        try
        {
            return await TryEvaluateAsync(projectPath, globalProperties, framework, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return await TryEvaluateAsync(projectPath, globalProperties, framework, cancellationToken);
        }
    }

    private static async Task<Evaluation> TryEvaluateAsync(string projectPath, IReadOnlyList<string> globalProperties, string? framework, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "msbuild", projectPath, "-nologo" };
        arguments.AddRange(globalProperties.Select(p => $"-p:{p}"));
        if (framework is not null)
        {
            arguments.Add($"-p:TargetFramework={framework}");
        }

        arguments.AddRange(Properties.Select(p => $"-getProperty:{p}"));
        arguments.AddRange(Items.Select(i => $"-getItem:{i}"));

        var result = await ProcessRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(projectPath)!, TimeSpan.FromMinutes(3), cancellationToken: cancellationToken);
        // The JSON alone: a notice the CLI prints before or after it is not part of it.
        var json = result.StandardOutput.Trim();
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (!result.Succeeded || start < 0 || end < start)
        {
            throw new InvalidOperationException($"Evaluation failed: {ProcessRunner.Tail(result.Output, 6)}");
        }

        using var document = JsonDocument.Parse(json[start..(end + 1)]);
        if (!document.RootElement.TryGetProperty("Properties", out var propertyElement))
        {
            // Without properties every project would silently be a library.
            throw new InvalidOperationException($"Evaluation returned no properties: {ProcessRunner.Tail(json, 6)}");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in propertyElement.EnumerateObject())
        {
            properties[property.Name] = property.Value.GetString() ?? "";
        }

        var items = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("Items", out var itemElement))
        {
            foreach (var group in itemElement.EnumerateObject())
            {
                items[group.Name] = [.. group.Value.EnumerateArray().Select(item =>
                    item.EnumerateObject().ToDictionary(m => m.Name, m => m.Value.ToString(), StringComparer.OrdinalIgnoreCase))];
            }
        }

        return new Evaluation(properties, items);
    }

    private static void Apply(ProjectInfo project, Dictionary<string, string> properties, Dictionary<string, List<Dictionary<string, string>>> items)
    {
        string? Property(string name) => properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        bool Flag(string name) => string.Equals(Property(name), "true", StringComparison.OrdinalIgnoreCase);
        List<Dictionary<string, string>> Group(string name) => items.TryGetValue(name, out var group) ? group : [];

        project.AssemblyName = Property("AssemblyName") ?? project.Name;
        project.OutputType = Property("OutputType");
        project.TargetPath = Property("TargetPath");
        project.ProjectAssetsFile = Property("ProjectAssetsFile");

        var centralVersions = Group("PackageVersion")
            .GroupBy(i => i["Identity"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().GetValueOrDefault("Version"), StringComparer.OrdinalIgnoreCase);

        project.PackageReferences = [.. Group("PackageReference")
            .Select(item => new PackageInfo
            {
                Id = item["Identity"],
                Requested = NonEmpty(item.GetValueOrDefault("VersionOverride"))
                            ?? NonEmpty(item.GetValueOrDefault("Version"))
                            ?? centralVersions.GetValueOrDefault(item["Identity"]),
                Implicit = string.Equals(item.GetValueOrDefault("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase) ? true : null,
            })
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];

        project.ProjectReferencePaths = [.. Group("ProjectReference")
            .Select(item => Path.GetFullPath(item.GetValueOrDefault("FullPath") ?? item["Identity"]))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        var frameworkReferences = Group("FrameworkReference").Select(i => i["Identity"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        project.FrameworkReferences = frameworkReferences.Count > 0 ? frameworkReferences : null;

        var reported = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ReportedProperties)
        {
            if (Property(name) is { } value)
            {
                reported[name] = value;
            }
        }

        project.Properties = reported.Count > 0 ? reported : null;

        bool HasPackage(params string[] ids) => project.PackageReferences.Any(p => ids.Contains(p.Id, StringComparer.OrdinalIgnoreCase));

        project.IsTestProject = Flag("IsTestProject")
                                || project.PackageReferences.Any(p => TestPackagePrefixes.Any(prefix => p.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                                || (project.Sdk?.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) ?? false);
        var executable = project.OutputType is { } outputType
                         && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

        // Tests first: a functional test project that starts its own Aspire resources references
        // Aspire.Hosting.AppHost too, and is not the solution's deployment.
        project.Kind = project.IsTestProject ? "test"
            : (Flag("IsAspireHost") || HasPackage("Aspire.Hosting.AppHost")) ? "aspire-host"
            : !executable ? "library"
            : Flag("UseMaui") || Flag("UseWPF") || Flag("UseWindowsForms") || Flag("UsingMicrosoftNETSdkBlazorWebAssembly")
              || string.Equals(project.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase)
              || (project.TargetFrameworks.Count > 0 && project.TargetFrameworks.All(f => f.Contains('-'))) ? "client"
            : Flag("UsingMicrosoftNETSdkWeb") || frameworkReferences.Contains("Microsoft.AspNetCore.App", StringComparer.OrdinalIgnoreCase) ? "web"
            : Flag("UsingMicrosoftNETSdkWorker") || HasPackage("Microsoft.Extensions.Hosting") ? "worker"
            : "console";
    }

    /// <summary>The SDK as the project file names it: Project/@Sdk, an Sdk element, or an Import with Sdk.</summary>
    internal static string? ReadSdk(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            return (string?)root?.Attribute("Sdk")
                   ?? (string?)root?.Elements("Sdk").FirstOrDefault()?.Attribute("Name")
                   ?? (string?)root?.Elements("Import").FirstOrDefault(i => i.Attribute("Sdk") is not null)?.Attribute("Sdk");
        }
        catch
        {
            return null;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

internal static class Frameworks
{
    /// <summary>The requested framework if the project has it, else the highest plain netX.Y one.</summary>
    public static string? Choose(IReadOnlyList<string> frameworks, string? requested)
    {
        if (frameworks.Count == 0)
        {
            return null;
        }

        if (requested is not null && frameworks.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            return requested;
        }

        return frameworks
            .OrderBy(f => f.Contains('-') ? 1 : 0)
            .ThenByDescending(VersionOf)
            .First();
    }

    /// <summary>The version a target framework moniker names: net8.0 is 8.0, netcoreapp3.1 3.1, net48 4.8; 0.0 for none.</summary>
    internal static Version VersionOf(string framework)
    {
        var digits = new string(framework.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!digits.Contains('.') && digits.Length > 1)
        {
            // netcoreapp-less monikers such as net48.
            digits = digits[0] + "." + digits[1..];
        }

        return System.Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }
}

internal static class AssetsReader
{
    /// <summary>The package versions a restore resolved for the project's top-level references.</summary>
    public static Dictionary<string, string> ResolvedVersions(string? assetsFile, string? framework)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (assetsFile is null || !File.Exists(assetsFile))
        {
            return versions;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
            if (!document.RootElement.TryGetProperty("targets", out var targets))
            {
                return versions;
            }

            var target = targets.EnumerateObject()
                .OrderBy(t => framework is not null && t.Name.Equals(framework, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.Name.Contains('/') ? 1 : 0)
                .Select(t => (JsonProperty?)t)
                .FirstOrDefault();
            if (target is null)
            {
                return versions;
            }

            foreach (var library in target.Value.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "package")
                {
                    var slash = library.Name.IndexOf('/');
                    if (slash > 0)
                    {
                        versions[library.Name[..slash]] = library.Name[(slash + 1)..];
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return versions;
    }

    /// <summary>The package folders restore used: what the probe passes as --additionalprobingpath.</summary>
    public static List<string> PackageFolders(string? assetsFile)
    {
        var folders = new List<string>();
        if (assetsFile is null || !File.Exists(assetsFile))
        {
            return folders;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
            if (document.RootElement.TryGetProperty("packageFolders", out var packageFolders))
            {
                folders.AddRange(packageFolders.EnumerateObject().Select(f => f.Name));
            }
        }
        catch (JsonException)
        {
        }

        return folders;
    }
}
