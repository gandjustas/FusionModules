using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace FusionModules.Tool;

internal sealed class ComposeService
{
    public string Name { get; set; } = "";

    public string? Image { get; set; }

    public string? BuildContext { get; set; }

    public string? Dockerfile { get; set; }

    public string? Command { get; set; }

    public int? Replicas { get; set; }

    public SortedDictionary<string, string?> Environment { get; set; } = new(StringComparer.Ordinal);

    public List<PortInfo> Ports { get; set; } = [];

    public List<string> DependsOn { get; set; } = [];
}

internal sealed record ComposeFiles(List<string> Files, List<string> Others);

internal static class ComposeDiscovery
{
    private static readonly string[] BaseNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];
    private static readonly string[] OverrideNames = ["compose.override.yaml", "compose.override.yml", "docker-compose.override.yaml", "docker-compose.override.yml"];
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "artifacts" };

    /// <summary>
    /// The compose files beside the solution: its own directory first, then the directories below it
    /// (two levels), then the directories above it up to the repository root. The first directory
    /// with a base file wins, and contributes the base and its override — what `docker compose up`
    /// itself reads by default. Other compose files there are reported, not merged.
    /// </summary>
    public static ComposeFiles? Discover(string root)
    {
        foreach (var directory in Candidates(root))
        {
            var files = Directory.EnumerateFiles(directory).ToList();
            var baseFile = BaseNames.Select(n => files.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null);
            if (baseFile is null)
            {
                continue;
            }

            var chosen = new List<string> { baseFile };
            var overrideFile = OverrideNames.Select(n => files.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null);
            if (overrideFile is not null)
            {
                chosen.Add(overrideFile);
            }

            var others = files
                .Where(f => IsComposeFile(Path.GetFileName(f)) && !chosen.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return new ComposeFiles(chosen, others);
        }

        return null;
    }

    private static bool IsComposeFile(string name) =>
        (name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compose.", StringComparison.OrdinalIgnoreCase))
        && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Candidates(string root)
    {
        yield return root;

        var level = new List<string> { root };
        for (var depth = 0; depth < 2; depth++)
        {
            level = [.. level.SelectMany(SafeDirectories).Where(d => !Skipped.Contains(Path.GetFileName(d)))];
            foreach (var directory in level)
            {
                yield return directory;
            }
        }

        // Upwards only inside a repository: never wander into the parent of every checkout.
        var ancestors = new List<string>();
        for (var current = Directory.GetParent(root); current is not null; current = current.Parent)
        {
            ancestors.Add(current.FullName);
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                foreach (var ancestor in ancestors)
                {
                    yield return ancestor;
                }

                yield break;
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal static class ComposeReader
{
    /// <summary>
    /// `docker compose config` when the CLI is there: it applies override files, .env, interpolation
    /// and every shorthand exactly as `up` would. Otherwise the files are read here, which covers the
    /// common subset — merge by service, ${VAR:-default} from .env and the environment.
    /// </summary>
    public static async Task<(List<ComposeService> Services, string ReadBy, string? Warning)> ReadAsync(IReadOnlyList<string> files, bool useDocker, CancellationToken cancellationToken)
    {
        string? warning = null;
        if (useDocker)
        {
            var directory = Path.GetDirectoryName(files[0])!;
            var arguments = new List<string> { "compose" };
            foreach (var file in files)
            {
                arguments.Add("-f");
                arguments.Add(file);
            }

            arguments.AddRange(["config", "--format", "json"]);
            try
            {
                var result = await ProcessRunner.RunAsync("docker", arguments, directory, TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
                if (result.Succeeded && result.StandardOutput.TrimStart().StartsWith('{'))
                {
                    return (FromConfigJson(result.StandardOutput), "docker-compose-config", null);
                }

                warning = $"docker compose config failed, read the files directly: {ProcessRunner.Tail(result.Output, 3)}";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No docker on the PATH.
            }
        }

        return (FromYaml(files), "yaml", warning);
    }

    internal static List<ComposeService> FromConfigJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var services = new List<ComposeService>();
        if (!document.RootElement.TryGetProperty("services", out var serviceElements))
        {
            return services;
        }

        foreach (var element in serviceElements.EnumerateObject())
        {
            var service = new ComposeService { Name = element.Name, Image = String(element.Value, "image") };
            if (element.Value.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
            {
                service.BuildContext = String(build, "context");
                var dockerfile = String(build, "dockerfile") ?? "Dockerfile";
                service.Dockerfile = service.BuildContext is null ? dockerfile : Path.GetFullPath(Path.Combine(service.BuildContext, dockerfile));
            }

            if (element.Value.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in environment.EnumerateObject())
                {
                    service.Environment[variable.Name] = variable.Value.ValueKind == JsonValueKind.Null ? null : variable.Value.ToString();
                }
            }

            if (element.Value.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
            {
                foreach (var port in ports.EnumerateArray())
                {
                    service.Ports.Add(new PortInfo
                    {
                        TargetPort = Int(port, "target"),
                        Port = Int(port, "published"),
                        Protocol = String(port, "protocol"),
                    });
                }
            }

            if (element.Value.TryGetProperty("depends_on", out var dependsOn) && dependsOn.ValueKind == JsonValueKind.Object)
            {
                service.DependsOn.AddRange(dependsOn.EnumerateObject().Select(d => d.Name));
            }

            if (element.Value.TryGetProperty("deploy", out var deploy) && deploy.ValueKind == JsonValueKind.Object)
            {
                service.Replicas = Int(deploy, "replicas");
            }

            service.Replicas ??= Int(element.Value, "scale");
            if (element.Value.TryGetProperty("command", out var command) && command.ValueKind != JsonValueKind.Null)
            {
                service.Command = command.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", command.EnumerateArray().Select(c => c.ToString()))
                    : command.ToString();
            }

            services.Add(service);
        }

        return services;
    }

    internal static List<ComposeService> FromYaml(IReadOnlyList<string> files)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
        var variables = DotEnv(Path.Combine(directory, ".env"));
        var deserializer = new DeserializerBuilder().Build();
        var merged = new Dictionary<string, Dictionary<object, object?>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = Interpolate(File.ReadAllText(file), variables);
            if (deserializer.Deserialize<object>(text) is not Dictionary<object, object?> root
                || !root.TryGetValue("services", out var servicesNode) || servicesNode is not Dictionary<object, object?> services)
            {
                continue;
            }

            foreach (var (key, value) in services)
            {
                var name = key.ToString()!;
                var definition = value as Dictionary<object, object?> ?? [];
                if (!merged.TryGetValue(name, out var existing))
                {
                    merged[name] = existing = [];
                }

                foreach (var (property, propertyValue) in definition)
                {
                    var propertyName = property.ToString()!;
                    existing[propertyName] = propertyName switch
                    {
                        "environment" => MergeEnvironment(existing.GetValueOrDefault(propertyName), propertyValue),
                        "ports" or "depends_on" when existing.GetValueOrDefault(propertyName) is List<object?> earlier && propertyValue is List<object?> later
                            => earlier.Concat(later).Distinct().ToList(),
                        _ => propertyValue,
                    };
                }
            }
        }

        var result = new List<ComposeService>();
        foreach (var (name, definition) in merged)
        {
            var service = new ComposeService { Name = name, Image = definition.GetValueOrDefault("image")?.ToString() };
            switch (definition.GetValueOrDefault("build"))
            {
                case string context:
                    service.BuildContext = Path.GetFullPath(Path.Combine(directory, context));
                    service.Dockerfile = Path.Combine(service.BuildContext, "Dockerfile");
                    break;
                case Dictionary<object, object?> build:
                    service.BuildContext = Path.GetFullPath(Path.Combine(directory, build.GetValueOrDefault("context")?.ToString() ?? "."));
                    service.Dockerfile = Path.GetFullPath(Path.Combine(service.BuildContext, build.GetValueOrDefault("dockerfile")?.ToString() ?? "Dockerfile"));
                    break;
            }

            foreach (var (variable, value) in EnvironmentMap(definition.GetValueOrDefault("environment")))
            {
                service.Environment[variable] = value;
            }

            if (definition.GetValueOrDefault("ports") is List<object?> ports)
            {
                service.Ports.AddRange(ports.Select(ParsePort).OfType<PortInfo>());
            }

            switch (definition.GetValueOrDefault("depends_on"))
            {
                case List<object?> list:
                    service.DependsOn.AddRange(list.Select(d => d?.ToString()).OfType<string>());
                    break;
                case Dictionary<object, object?> map:
                    service.DependsOn.AddRange(map.Keys.Select(k => k.ToString()!));
                    break;
            }

            if (definition.GetValueOrDefault("deploy") is Dictionary<object, object?> deploy && int.TryParse(deploy.GetValueOrDefault("replicas")?.ToString(), out var replicas))
            {
                service.Replicas = replicas;
            }
            else if (int.TryParse(definition.GetValueOrDefault("scale")?.ToString(), out var scale))
            {
                service.Replicas = scale;
            }

            service.Command = definition.GetValueOrDefault("command") switch
            {
                List<object?> parts => string.Join(" ", parts),
                { } command => command.ToString(),
                null => null,
            };

            result.Add(service);
        }

        return result;
    }

    private static object MergeEnvironment(object? earlier, object? later)
    {
        var merged = new Dictionary<object, object?>();
        foreach (var (name, value) in EnvironmentMap(earlier).Concat(EnvironmentMap(later)))
        {
            merged[name] = value;
        }

        return merged;
    }

    private static IEnumerable<KeyValuePair<string, string?>> EnvironmentMap(object? node)
    {
        switch (node)
        {
            case Dictionary<object, object?> map:
                foreach (var (key, value) in map)
                {
                    yield return new(key.ToString()!, value?.ToString());
                }

                break;
            case List<object?> list:
                foreach (var entry in list.Select(e => e?.ToString()).OfType<string>())
                {
                    var equals = entry.IndexOf('=');
                    yield return equals < 0 ? new(entry, null) : new(entry[..equals], entry[(equals + 1)..]);
                }

                break;
        }
    }

    private static PortInfo? ParsePort(object? node)
    {
        if (node is Dictionary<object, object?> map)
        {
            return new PortInfo
            {
                TargetPort = int.TryParse(map.GetValueOrDefault("target")?.ToString(), out var t) ? t : null,
                Port = int.TryParse(map.GetValueOrDefault("published")?.ToString(), out var p) ? p : null,
                Protocol = map.GetValueOrDefault("protocol")?.ToString(),
            };
        }

        var text = node?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? protocol = null;
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            protocol = text[(slash + 1)..];
            text = text[..slash];
        }

        // [ip:]published:target, or target alone. Ranges keep their first port.
        var parts = text.Split(':');
        static int? First(string part) => int.TryParse(part.Split('-')[0], out var value) ? value : null;
        return parts.Length == 1
            ? new PortInfo { TargetPort = First(parts[0]), Protocol = protocol }
            : new PortInfo { Port = First(parts[^2]), TargetPort = First(parts[^1]), Protocol = protocol };
    }

    internal static Dictionary<string, string> DotEnv(string path)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return variables;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals > 0)
            {
                variables[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim().Trim('"', '\'');
            }
        }

        return variables;
    }

    private static readonly Regex Variable = new(@"\$\$|\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?[-?+])(?<arg>[^}]*))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Compose interpolation: the shell's environment wins over .env, as in compose itself.</summary>
    internal static string Interpolate(string text, IReadOnlyDictionary<string, string> dotEnv) =>
        Variable.Replace(text, match =>
        {
            if (match.Value == "$$")
            {
                return "$";
            }

            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["bare"].Value;
            var value = System.Environment.GetEnvironmentVariable(name) ?? dotEnv.GetValueOrDefault(name);
            var op = match.Groups["op"].Value;
            var argument = match.Groups["arg"].Value;
            return op switch
            {
                ":-" => string.IsNullOrEmpty(value) ? argument : value,
                "-" => value ?? argument,
                ":+" => string.IsNullOrEmpty(value) ? "" : argument,
                "+" => value is null ? "" : argument,
                _ => value ?? "",
            };
        });

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && int.TryParse(value.ToString(), out var number) ? number : null;
}

internal sealed record DockerfileFacts(string? EntrypointAssembly, List<string> BuiltProjects);

internal static class DockerfileReader
{
    public static DockerfileFacts Read(string path)
    {
        string? entrypoint = null;
        var projects = new List<string>();
        foreach (var line in LogicalLines(File.ReadAllLines(path)))
        {
            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0)
            {
                continue;
            }

            var instruction = line[..space].ToUpperInvariant();
            var rest = line[(space + 1)..].Trim();
            if (instruction is "ENTRYPOINT" or "CMD")
            {
                var dll = Tokens(rest).LastOrDefault(t => t.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                if (dll is not null)
                {
                    entrypoint = Path.GetFileNameWithoutExtension(dll.Replace('\\', '/').Split('/')[^1]);
                }
            }
            else if (instruction == "RUN")
            {
                var tokens = Tokens(rest).ToList();
                for (var i = 0; i + 1 < tokens.Count; i++)
                {
                    if (tokens[i] == "dotnet" && tokens[i + 1] is "publish" or "build")
                    {
                        var project = tokens.Skip(i + 2).TakeWhile(t => t is not "&&" and not ";")
                            .FirstOrDefault(InputResolver.IsProjectFile);
                        if (project is not null)
                        {
                            projects.Add(project);
                        }
                    }
                }
            }
        }

        return new DockerfileFacts(entrypoint, projects);
    }

    private static IEnumerable<string> LogicalLines(IEnumerable<string> lines)
    {
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                current.Append(line[..^1]).Append(' ');
                continue;
            }

            current.Append(line);
            yield return current.ToString().Trim();
            current.Clear();
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static IEnumerable<string> Tokens(string text)
    {
        if (text.StartsWith('['))
        {
            string[]? array = null;
            try
            {
                array = JsonSerializer.Deserialize<string[]>(text);
            }
            catch (JsonException)
            {
            }

            if (array is not null)
            {
                return array;
            }
        }

        return text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\''));
    }

    /// <summary>
    /// Which project an image is built from: the assembly its ENTRYPOINT runs, else the project
    /// `dotnet publish` / `dotnet build` names, else the project beside the Dockerfile — never a test
    /// or a library project, which no image runs.
    /// </summary>
    public static (ProjectInfo? Project, string MappedBy, string? Evidence) Map(string? dockerfile, IReadOnlyCollection<ProjectInfo> projects)
    {
        if (dockerfile is null || !File.Exists(dockerfile))
        {
            return (null, "none", dockerfile is null ? null : $"{dockerfile} not found");
        }

        var candidates = projects.Where(p => p.Kind is not ("test" or "library")).ToList();
        var facts = Read(dockerfile);

        if (facts.EntrypointAssembly is { } assembly
            && candidates.FirstOrDefault(p => string.Equals(p.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase)) is { } byAssembly)
        {
            return (byAssembly, "entrypoint", $"{assembly}.dll");
        }

        foreach (var built in facts.BuiltProjects.AsEnumerable().Reverse())
        {
            var fileName = built.Replace('\\', '/').Split('/')[^1];
            var matches = candidates.Where(p => Path.GetFileName(p.FullPath).Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0)
            {
                var normalized = built.Replace('\\', '/').TrimStart('.', '/');
                var best = matches.FirstOrDefault(p => p.FullPath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase)) ?? matches[0];
                return (best, "publish", built);
            }
        }

        var directory = Path.GetDirectoryName(dockerfile)!;
        if (candidates.FirstOrDefault(p => string.Equals(Path.GetDirectoryName(p.FullPath), directory, StringComparison.OrdinalIgnoreCase)) is { } beside)
        {
            return (beside, "dockerfile-directory", Path.GetFileName(beside.FullPath));
        }

        return (null, "none", facts.EntrypointAssembly is null ? null : $"ENTRYPOINT runs {facts.EntrypointAssembly}.dll, which no project in the solution builds");
    }
}

/// <summary>
/// Ties a compose service that names an image and builds nothing — the image is built elsewhere,
/// by CI — to the project the image is of: by the service's name, else the image's name without
/// registry and tag, against each executable project's name, assembly name and the assembly its
/// Dockerfile's ENTRYPOINT runs. Case, '-', '_' and '.' are ignored: catalog-api is Catalog.Api.
/// </summary>
internal static class ImageMapper
{
    public static (ProjectInfo? Project, string? MappedBy, string? Evidence) Map(string service, string? image, IReadOnlyCollection<ProjectInfo> executables)
    {
        // A database or a broker by its image is infrastructure, whatever the service is called.
        if (Infrastructure.Classify(image, service) != "container")
        {
            return (null, null, null);
        }

        var names = executables.Select(p => (Project: p, Names: NamesOf(p))).ToList();
        foreach (var (key, mappedBy) in new[] { (service, "service-name"), (ImageName(image), "image-name") })
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var normalized = Normalize(key);
            var matches = names
                .Select(n => (n.Project, Name: n.Names.FirstOrDefault(name => Normalize(name.Value) == normalized)))
                .Where(m => m.Name.Description is not null)
                .ToList();
            if (matches.Count == 1)
            {
                return (matches[0].Project, mappedBy, $"{key} matches {matches[0].Name.Description}");
            }

            if (matches.Count > 1)
            {
                return (null, null, $"{key} matches several projects: {string.Join(", ", matches.Select(m => m.Project.Name))}");
            }
        }

        return (null, null, null);
    }

    /// <summary>The image's own name: no registry or user, no tag, no digest. ${REGISTRY}/catalog-api:latest is catalog-api.</summary>
    internal static string? ImageName(string? image) =>
        Repository(image) is { } repository ? repository[(repository.LastIndexOf('/') + 1)..] : null;

    /// <summary>The image without tag or digest, registry and path kept: mcr.microsoft.com/mssql/server:2022-latest is mcr.microsoft.com/mssql/server.</summary>
    internal static string? Repository(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var name = image.Split('@')[0];
        // A colon after the last '/' starts the tag; one before it is a registry's port.
        var colon = name.LastIndexOf(':');
        return colon > name.LastIndexOf('/') ? name[..colon] : name;
    }

    internal static string Normalize(string name) => new string(name.Where(c => c is not ('-' or '_' or '.')).ToArray()).ToLowerInvariant();

    private static List<(string Value, string Description)> NamesOf(ProjectInfo project)
    {
        var names = new List<(string Value, string Description)> { (project.Name, $"project {project.Name}") };
        if (project.AssemblyName is { } assembly && assembly != project.Name)
        {
            names.Add((assembly, $"the assembly name of {project.Name}"));
        }

        var dockerfile = Path.Combine(Path.GetDirectoryName(project.FullPath) ?? "", "Dockerfile");
        try
        {
            if (File.Exists(dockerfile) && DockerfileReader.Read(dockerfile).EntrypointAssembly is { } entrypoint)
            {
                names.Add((entrypoint, $"{entrypoint}.dll, which the Dockerfile beside {project.Name} runs"));
            }
        }
        catch (IOException)
        {
            // An unreadable Dockerfile says nothing.
        }

        return names;
    }
}

internal static class Infrastructure
{
    /// <summary>
    /// What an image is; by the service's name when the image is left empty — <c>image: ${MYSQL_IMAGE}</c>
    /// with nothing to interpolate it from.
    /// </summary>
    public static string Classify(string? image, string service) =>
        string.IsNullOrWhiteSpace(ImageMapper.ImageName(image)) ? Classify(service) : Classify(image);

    /// <summary>What an image is, by the words of its name, registry and path included (<see cref="ResourceKinds.OfName"/>); "container" when none says.</summary>
    public static string Classify(string? image) => ResourceKinds.OfName(ImageMapper.Repository(image)) ?? "container";
}
