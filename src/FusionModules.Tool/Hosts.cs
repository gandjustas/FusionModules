using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// Where restore and build output go: never the solution's own bin/ and obj/. MSBuild's artifacts
/// layout (UseArtifactsOutput + ArtifactsPath, as global properties) gives every project its own
/// directory under one root, so building several hosts that share libraries needs no coordination.
/// </summary>
internal sealed class Workspace
{
    public Workspace(string directory, string configuration)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        GlobalProperties =
        [
            "UseArtifactsOutput=true",
            $"ArtifactsPath={Path.Combine(directory, "artifacts")}",
            $"Configuration={configuration}",
            // Build-time OpenAPI generation writes its document into the project directory, not the
            // output: the one common build step that would change the solution's own files.
            "OpenApiGenerateDocuments=false",
            "OpenApiGenerateDocumentsOnBuild=false",
        ];
    }

    public string Directory { get; }

    public IReadOnlyList<string> GlobalProperties { get; }

    public static string DefaultFor(string input)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(input).ToLowerInvariant())))[..10];
        return Path.Combine(Path.GetTempPath(), "fusion-modules-inventory", $"{Path.GetFileNameWithoutExtension(input)}-{hash}".ToLowerInvariant());
    }
}

internal static partial class HostBuilder
{
    public static async Task<ProcessResult> RestoreAsync(string target, Workspace workspace, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "restore", target, "-nologo", "-v:q" };
        arguments.AddRange(workspace.GlobalProperties.Select(p => $"-p:{p}"));
        return await ProcessRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(target)!, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds the given projects in one MSBuild invocation through a generated traversal project, so
    /// that shared libraries are built once and in parallel. Returns, per project, the build errors
    /// that concern it or anything it references; an empty list means it built.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> BuildAsync(
        IReadOnlyList<ProjectInfo> projects, IReadOnlyDictionary<string, ProjectInfo> all, string root, Workspace workspace, string? framework, CancellationToken cancellationToken)
    {
        var errors = projects.ToDictionary(p => p.FullPath, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        if (projects.Count == 0)
        {
            return errors;
        }

        var traversal = new XElement("Project",
            new XElement("ItemGroup", projects.Select(p =>
            {
                var item = new XElement("HostProject", new XAttribute("Include", p.FullPath));
                if (p.TargetFrameworks.Count > 1)
                {
                    item.Add(new XAttribute("AdditionalProperties", $"TargetFramework={Frameworks.Choose(p.TargetFrameworks, framework)}"));
                }

                return item;
            })),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("MSBuild",
                    new XAttribute("Projects", "@(HostProject)"),
                    new XAttribute("Targets", "Build"),
                    new XAttribute("BuildInParallel", "true"),
                    new XAttribute("ContinueOnError", "true"))));

        var path = Path.Combine(workspace.Directory, $"build-{Guid.NewGuid():N}.proj");
        await File.WriteAllTextAsync(path, traversal.ToString(), cancellationToken);

        var arguments = new List<string> { "msbuild", path, "-nologo", "-m", "-v:m", "-clp:NoSummary;ErrorsOnly" };
        arguments.AddRange(workspace.GlobalProperties.Select(p => $"-p:{p}"));
        ProcessResult result;
        try
        {
            // The solution's directory, so that its global.json chooses the SDK.
            result = await ProcessRunner.RunAsync("dotnet", arguments, root, TimeSpan.FromMinutes(20), cancellationToken: cancellationToken);
        }
        finally
        {
            File.Delete(path);
        }

        var failed = ErrorLines(result.Output)
            .Select(l => (Line: l, Match: ProjectSuffix().Match(l)))
            .ToList();

        foreach (var project in projects)
        {
            var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.FullPath };
            foreach (var reference in project.TransitiveProjectReferences)
            {
                scope.Add(Path.GetFullPath(Path.Combine(root, reference)));
            }

            errors[project.FullPath].AddRange(failed
                .Where(f => f.Match.Success && scope.Contains(f.Match.Groups[1].Value))
                .Select(f => f.Line)
                .Distinct()
                .Take(10));

            if (errors[project.FullPath].Count == 0 && (project.TargetPath is null || !File.Exists(project.TargetPath)))
            {
                errors[project.FullPath].Add(result.Succeeded
                    ? $"The build succeeded but produced no {project.TargetPath}."
                    : $"The build failed: {ProcessRunner.Tail(result.Output, 8)}");
            }
        }

        return errors;
    }

    /// <summary>The lines of MSBuild's output that report an error: "path(1,2): error CS1002: … [project]".</summary>
    public static IEnumerable<string> ErrorLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains(": error ", StringComparison.Ordinal));

    [GeneratedRegex(@"\[([^\[\]]+\.(?:csproj|fsproj|vbproj))(?:::[^\]]*)?\]\s*$")]
    private static partial Regex ProjectSuffix();
}

internal static class HostProber
{
    public static string ProbeAssembly => Path.Combine(AppContext.BaseDirectory, "probe", "FusionModules.Tool.Probe.dll");

    /// <summary>
    /// Whether the build output is something HostFactoryResolver can capture: it has to use
    /// Microsoft.Extensions.Hosting — anything else would simply be run, which the probe must never
    /// do — on a runtime that can load the probe. An application older than the probe (net5.0 to
    /// net7.0, netcoreapp3.x) is run rolled forward onto the newest runtime installed: its shared
    /// frameworks roll forward with it, and Microsoft.Extensions.Hosting is then one that raises the
    /// events the probe listens to. Only .NET Framework cannot run at all.
    /// </summary>
    public static HostCheck Check(string targetPath, string? targetFramework = null)
    {
        if (targetFramework is not null && IsNetFramework(targetFramework))
        {
            return new HostCheck($"It targets {targetFramework}, .NET Framework: the probe runs in .NET's runtime, which cannot load it. Static information only.", null);
        }

        var runtimeConfig = Path.ChangeExtension(targetPath, ".runtimeconfig.json");
        var deps = Path.ChangeExtension(targetPath, ".deps.json");
        if (!File.Exists(targetPath) || !File.Exists(runtimeConfig) || !File.Exists(deps))
        {
            return new HostCheck($"No build output at {targetPath} (with .deps.json and .runtimeconfig.json).", null);
        }

        string? rollForwardFrom = null;
        var runtimeConfigText = File.ReadAllText(runtimeConfig);
        using (var config = JsonDocument.Parse(runtimeConfigText))
        {
            if (config.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                && runtimeOptions.TryGetProperty("tfm", out var tfm)
                && tfm.GetString() is { } moniker
                && moniker.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                && (moniker.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) || Frameworks.VersionOf(moniker).Major is > 0 and < 8))
            {
                rollForwardFrom = moniker;
            }
        }

        if (runtimeConfigText.Contains("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)
            || File.ReadAllText(deps).Contains("\"Microsoft.Extensions.Hosting/", StringComparison.OrdinalIgnoreCase))
        {
            return new HostCheck(null, rollForwardFrom);
        }

        return new HostCheck("It does not use Microsoft.Extensions.Hosting, so there is no host to capture; running it would run the program.", null);
    }

    /// <summary>net48, net472, net20: a .NET Framework moniker has no dot and no "coreapp".</summary>
    internal static bool IsNetFramework(string targetFramework) =>
        targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && targetFramework.Length is 5 or 6
        && targetFramework[3..].All(char.IsDigit);

    /// <summary>
    /// Probes the build output of <paramref name="project"/> as <paramref name="name"/> — the
    /// service, or the AppHost — in a process of its own, in the application's runtime.
    /// </summary>
    public static async Task<ProbeOutput> ProbeAsync(
        ProjectInfo project, string name, string mode, IReadOnlyDictionary<string, string> environment, Workspace workspace, InventoryOptions options, CancellationToken cancellationToken)
    {
        var targetPath = project.TargetPath!;
        var check = Check(targetPath, Frameworks.Choose(project.TargetFrameworks, options.Framework));
        if (check.WhyNot is { } reason)
        {
            return new ProbeOutput { Mode = mode, Status = "skipped", Errors = [ProbeError.Of("probe", reason)] };
        }

        var output = Path.Combine(workspace.Directory, "probes", $"{project.Name}-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var arguments = new List<string> { "exec" };
        if (check.RollForwardFrom is not null)
        {
            // The probe needs .NET 8 or later; the newest runtime installed runs it, and the application.
            arguments.AddRange(["--roll-forward", "LatestMajor"]);
        }

        arguments.AddRange(
        [
            "--depsfile", Path.ChangeExtension(targetPath, ".deps.json"),
            "--runtimeconfig", Path.ChangeExtension(targetPath, ".runtimeconfig.json"),
        ]);
        foreach (var folder in AssetsReader.PackageFolders(project.ProjectAssetsFile))
        {
            arguments.AddRange(["--additionalprobingpath", folder.TrimEnd('\\', '/')]);
        }

        arguments.AddRange([ProbeAssembly, "--assembly", targetPath, "--output", output, "--mode", mode,
            "--timeout", options.ProbeTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--fake-address", FakeConfiguration.Address]);
        if (options.AllServices)
        {
            arguments.Add("--all-services");
        }

        // The content root is the project directory, as under `dotnet run`, so appsettings.json and
        // user secrets are found; the working directory is a scratch one, so that whatever the
        // application writes to a relative path (a SQLite file, a log) does not land in the solution.
        var scratch = WorkingDirectory(workspace, name);
        Directory.CreateDirectory(scratch);
        var projectDirectory = Path.GetDirectoryName(project.FullPath)!;
        var withContentRoot = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
        {
            ["ASPNETCORE_CONTENTROOT"] = projectDirectory,
            ["DOTNET_CONTENTROOT"] = projectDirectory,
        };

        var result = await ProcessRunner.RunAsync(
            "dotnet", arguments, scratch, options.ProbeTimeout + TimeSpan.FromSeconds(30), withContentRoot, cancellationToken, application: true);

        // The probe writes its result beside and renames it into place, so a result is whole or not
        // there; a probe killed mid-write leaves only the temporary file.
        File.Delete(output + ".tmp");
        var probe = ReadResult(output, mode) ?? new ProbeOutput
        {
            Mode = mode,
            Status = result.TimedOut ? "timeout" : "failed",
            Errors = [ProbeError.Of("process", result.TimedOut
                ? $"The probe process did not exit within {options.ProbeTimeout.TotalSeconds + 30:0}s and was killed."
                : $"The probe process exited with {result.ExitCode} and wrote nothing.")],
        };

        if (probe.Status is not "started" && result.Output.Trim().Length > 0)
        {
            probe.Errors.Add(new ProbeError { Stage = "output", Message = "The last lines the application wrote.", Detail = ProcessRunner.Tail(result.Output, 12) });
        }

        probe.RolledForwardFrom = check.RollForwardFrom;
        if (check.RollForwardFrom is not null && probe.Errors.FirstOrDefault(e => e.Stage == "resolve") is { } resolve)
        {
            resolve.Hint = $"It targets {check.RollForwardFrom} and was rolled forward onto {probe.Runtime ?? "a newer runtime"}, but its Microsoft.Extensions.Hosting "
                           + "is an app-local package older than 6.0, which raises none of the events the probe hooks. Static information only.";
        }

        return probe;
    }

    /// <summary>The probe's result file, deleted once read; null when there is none. A file that is not a result is an error of the probe's, not the run's end.</summary>
    internal static ProbeOutput? ReadResult(string path, string mode)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProbeOutput>(File.ReadAllText(path), JsonFormat.Options) ?? throw new JsonException("The result is null.");
        }
        catch (JsonException exception)
        {
            return new ProbeOutput { Mode = mode, Status = "failed", Errors = [ProbeError.Of("process", $"The probe's result could not be read: {exception.Message}")] };
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The probed process's working directory: one per service, by its name — two services built
    /// from one project are probed at the same time, and must not share a SQLite file or a log.
    /// </summary>
    internal static string WorkingDirectory(Workspace workspace, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()).Trim('.', ' ');
        return Path.Combine(workspace.Directory, "cwd", safe.Length == 0 ? "_" : safe);
    }
}

/// <summary>Why a build output cannot be probed, if it cannot; and the framework it is rolled forward from, if it is.</summary>
internal sealed record HostCheck(string? WhyNot, string? RollForwardFrom);
