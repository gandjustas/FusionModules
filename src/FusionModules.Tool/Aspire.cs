using System.Text.Json;
using System.Text.RegularExpressions;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

internal static partial class AspireModel
{
    /// <summary>
    /// What the AppHost adds for its own development loop rather than for the application: the
    /// project rebuilders and the shared build step of Aspire 13. Kept in "aspire", left out of the
    /// deployment.
    /// </summary>
    private static readonly HashSet<string> Tooling = new(StringComparer.Ordinal) { "ProjectRebuilderResource", "DotnetProjectBuildResource" };

    public static bool IsTooling(ProbeAspireResource resource) => resource.Name.StartsWith("__", StringComparison.Ordinal) || Tooling.Contains(resource.Type);

    /// <summary>The AppHost's resources as deployment services.</summary>
    public static List<DeploymentService> Services(ProbeAspireModel model, Func<string, string> relative)
    {
        var names = model.Resources.Where(r => !IsTooling(r)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var services = new List<DeploymentService>();
        foreach (var resource in model.Resources.Where(r => !IsTooling(r)))
        {
            var service = new DeploymentService
            {
                Name = resource.Name,
                Type = resource.Type,
                Kind = resource.ProjectPath is not null ? "project"
                    : resource.Type.StartsWith("Parameter", StringComparison.Ordinal) || resource.Type.StartsWith("parameter", StringComparison.Ordinal) ? "parameter"
                    : resource.Image is not null || resource.Dockerfile is not null ? "container"
                    : resource.Command is not null ? "executable"
                    : resource.ConnectionString is not null ? "infrastructure"
                    : "other",
                Project = ProjectFile(resource.ProjectPath) is { } project ? relative(project) : null,
                Image = resource.Image,
                Build = resource.Dockerfile is null ? null : new BuildInfo { Context = Relative(resource.DockerfileContext, relative), Dockerfile = Relative(resource.Dockerfile, relative) },
                Command = resource.Command,
                Parent = resource.Parent,
                ConnectionString = resource.ConnectionString,
                Replicas = resource.Replicas,
                Environment = resource.Environment,
                Ports = resource.Endpoints?.Select(e => new PortInfo
                {
                    Name = e.Name,
                    Scheme = e.Scheme,
                    Protocol = e.Transport,
                    Port = e.Port,
                    TargetPort = e.TargetPort,
                    External = e.External,
                }).ToList(),
            };

            var references = new List<ResourceReference>();
            foreach (var relationship in (resource.Relationships ?? []).Where(r => names.Contains(r.Resource)))
            {
                Add(references, relationship.Resource, relationship.Type);
            }

            foreach (var value in resource.Environment?.Values ?? Enumerable.Empty<string?>())
            {
                foreach (Match match in ExpressionName().Matches(value ?? ""))
                {
                    var name = match.Groups[1].Value;
                    if (name != resource.Name && names.Contains(name))
                    {
                        Add(references, name, "environment");
                    }
                }
            }

            service.References = references.Count > 0 ? references : null;
            services.Add(service);
        }

        return services;
    }

    private static void Add(List<ResourceReference> references, string name, string type)
    {
        if (!references.Any(r => r.Name == name && r.Type == type))
        {
            references.Add(new ResourceReference { Name = name, Type = type });
        }
    }

    private static string? Relative(string? path, Func<string, string> relative) => path is null ? null : relative(path);

    /// <summary>Aspire 13's AddCSharpApp/AddDotnetProject may name a directory; the project is the one file in it.</summary>
    public static string? ProjectFile(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            var projects = Directory.EnumerateFiles(path).Where(InputResolver.IsProjectFile).ToList();
            return projects.Count == 1 ? Path.GetFullPath(projects[0]) : path;
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// The environment a project resource's host is probed with: its own variables, with every
    /// manifest expression replaced by a fake that parses. Returns the names it had to invent.
    /// </summary>
    public static (Dictionary<string, string> Environment, List<string> Faked) ProbeEnvironment(ProbeAspireResource resource, ProbeAspireModel model)
    {
        var byName = model.Resources.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var faked = new List<string>();
        foreach (var (name, value) in resource.Environment ?? [])
        {
            if (value is null)
            {
                continue;
            }

            if (FakeConfiguration.HasExpression(value))
            {
                var resolved = FakeConfiguration.Resolve(value, byName, resource.Name);
                environment[name] = name.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase) ? FakeConfiguration.DistinctPorts(resolved) : resolved;
                faked.Add(name);
            }
            else
            {
                environment[name] = value;
            }
        }

        return (environment, faked);
    }

    /// <summary>
    /// Aspire 8 and 9 can write their model as a manifest: `--publisher manifest`. The fallback when
    /// the probe could not read the model; Aspire 13 no longer writes it this way. It runs the
    /// AppHost's Program to its end, in publish mode. Returns the model, or why there is none: the
    /// last lines the publisher wrote.
    /// </summary>
    public static async Task<(ProbeAspireModel? Model, string? Failure)> FromManifestAsync(ProjectInfo appHost, Workspace workspace, CancellationToken cancellationToken)
    {
        if (appHost.TargetPath is null || !File.Exists(appHost.TargetPath))
        {
            return (null, "no build output");
        }

        var manifest = Path.Combine(workspace.Directory, $"aspire-manifest-{Guid.NewGuid():N}.json");
        var result = await ProcessRunner.RunAsync(
            "dotnet", [appHost.TargetPath, "--publisher", "manifest", "--output-path", manifest],
            Path.GetDirectoryName(appHost.FullPath)!, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken, application: true);
        if (!File.Exists(manifest))
        {
            var tail = ProcessRunner.Tail(result.Output, 3).Replace('\n', ' ');
            return (null, result.TimedOut
                ? "the manifest publisher did not finish in 2 minutes"
                : $"the manifest publisher exited with {result.ExitCode}{(tail.Length > 0 ? ": " + tail : "")}");
        }

        try
        {
            return (ParseManifest(await File.ReadAllTextAsync(manifest, cancellationToken), Path.GetDirectoryName(manifest)!), null);
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    internal static ProbeAspireModel ParseManifest(string json, string manifestDirectory)
    {
        using var document = JsonDocument.Parse(json);
        var model = new ProbeAspireModel { Version = "manifest" };
        if (!document.RootElement.TryGetProperty("resources", out var resources))
        {
            return model;
        }

        foreach (var element in resources.EnumerateObject())
        {
            var value = element.Value;
            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var resource = new ProbeAspireResource
            {
                Name = element.Name,
                Type = Text(value, "type") ?? "?",
                Image = Text(value, "image"),
                ConnectionString = Text(value, "connectionString"),
                Parent = Text(value, "parent"),
                Command = Text(value, "command"),
            };

            if (Text(value, "path") is { } path && resource.Type.StartsWith("project", StringComparison.Ordinal))
            {
                resource.ProjectPath = Path.GetFullPath(Path.Combine(manifestDirectory, path));
            }

            if (value.TryGetProperty("build", out var build))
            {
                resource.DockerfileContext = Text(build, "context") is { } context ? Path.GetFullPath(Path.Combine(manifestDirectory, context)) : null;
                resource.Dockerfile = Text(build, "dockerfile") is { } dockerfile ? Path.GetFullPath(Path.Combine(manifestDirectory, dockerfile)) : null;
            }

            if (value.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                resource.Environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
                foreach (var variable in env.EnumerateObject())
                {
                    resource.Environment[variable.Name] = variable.Value.ToString();
                }
            }

            if (value.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
            {
                resource.Endpoints = [.. bindings.EnumerateObject().Select(b => new ProbeAspireEndpoint
                {
                    Name = b.Name,
                    Scheme = Text(b.Value, "scheme"),
                    Transport = Text(b.Value, "transport"),
                    Port = b.Value.TryGetProperty("port", out var port) && port.TryGetInt32(out var p) ? p : null,
                    TargetPort = b.Value.TryGetProperty("targetPort", out var target) && target.TryGetInt32(out var t) ? t : null,
                    External = b.Value.TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.True,
                })];
            }

            if (resource.Parent is not null)
            {
                resource.Relationships = [new ProbeAspireRelationship { Resource = resource.Parent, Type = "Parent" }];
            }

            model.Resources.Add(resource);
        }

        return model;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_\-]+)[\.\}]")]
    private static partial Regex ExpressionName();
}
