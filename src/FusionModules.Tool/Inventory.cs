using System.Diagnostics;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// The whole run: find the solution and its projects, find how it is deployed, build what runs and
/// probe each host. Every failure past the input is recorded in the document and the run goes on —
/// one service that does not build or start must not cost the inventory of the others.
/// </summary>
internal sealed class Inventory(InventoryOptions options, Action<string> log)
{
    private static readonly string[] HostKinds = ["web", "worker", "console"];

    private readonly InventoryDocument document = new();
    private readonly Dictionary<string, ProjectInfo> projects = new(StringComparer.OrdinalIgnoreCase);
    private ResolvedInput input = null!;
    private Workspace workspace = null!;

    public async Task<InventoryDocument> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        input = InputResolver.Resolve(options.Path);
        var target = input.Solution ?? input.Project!;
        workspace = new Workspace(options.WorkDirectory is null ? Workspace.DefaultFor(target) : Path.GetFullPath(options.WorkDirectory), options.Configuration);

        document.Tool = $"FusionModules.Tool {Program.Version}";
        document.GeneratedAtUtc = DateTimeOffset.UtcNow;
        document.Root = input.Root;
        document.Input = new InputInfo
        {
            Path = target,
            Kind = input.Solution is not null ? "solution" : "project",
            Solution = input.Solution is null ? null : Relative(input.Solution),
            Project = input.Project is null ? null : Relative(input.Project),
        };

        await EvaluateProjectsAsync(cancellationToken);

        if (!options.Static)
        {
            log($"Restoring {Path.GetFileName(target)} into {workspace.Directory}");
            var restore = await HostBuilder.RestoreAsync(target, workspace, cancellationToken);
            if (!restore.Succeeded)
            {
                var errors = HostBuilder.ErrorLines(restore.Output).Distinct().Take(10).ToList();
                var detail = errors.Count > 0 ? string.Join("\n", errors) : ProcessRunner.Tail(restore.Output, 6);
                Error("restore", Path.GetFileName(target), $"Restore reported errors; the projects it could restore are still built.\n{detail}");
            }

            foreach (var project in projects.Values)
            {
                var resolved = AssetsReader.ResolvedVersions(project.ProjectAssetsFile, Frameworks.Choose(project.TargetFrameworks, options.Framework));
                foreach (var package in project.PackageReferences)
                {
                    package.Resolved = resolved.GetValueOrDefault(package.Id);
                }
            }
        }

        await DiscoverDeploymentAsync(cancellationToken);
        await InventoryHostsAsync(cancellationToken);

        // Collisions compare the real values — two different passwords are a collision — and only
        // then is anything secret masked, everywhere the document repeats configuration.
        document.Collisions = Collisions.Find(document.Hosts);
        Redact(document);
        document.DurationMs = stopwatch.ElapsedMilliseconds;
        return document;
    }

    /// <summary>
    /// Masks secrets wherever the document carries configuration — a value under a secret's name
    /// (Password, ClientSecret, ApiKey, Jwt:Key) whole, the secret pairs of a connection string, a
    /// URI's password — and in its free text: commands, errors, notes. Free text is also searched
    /// for every secret value the configuration holds, and the shell's secret-named variables.
    /// </summary>
    internal static void Redact(InventoryDocument document)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (Secrets.IsSecretName((string)variable.Key))
            {
                Secrets.CollectSecrets((string)variable.Key, variable.Value as string, known);
            }
        }

        static void MaskAll<TValue>(IDictionary<string, TValue>? values, Func<string, TValue, TValue> mask)
        {
            foreach (var key in values?.Keys.ToList() ?? [])
            {
                values![key] = mask(key, values[key]);
            }
        }

        void Collect(IEnumerable<(string Key, string? Value)> values)
        {
            foreach (var (key, value) in values)
            {
                Secrets.CollectSecrets(key, value, known);
            }
        }

        // Every secret first, then every mask: an error of one host may quote another's password.
        foreach (var service in document.Deployment.Services)
        {
            Collect((service.Environment ?? []).Select(e => (e.Key, e.Value)).Append(("", service.ConnectionString)));
        }

        foreach (var host in document.Hosts)
        {
            Collect((host.Environment ?? []).Select(e => (e.Key, (string?)e.Value)));
            Collect((host.Configuration?.Values ?? Enumerable.Empty<SortedDictionary<string, string?>>()).SelectMany(f => f.Select(e => (e.Key, e.Value))));
        }

        foreach (var resource in document.Aspire?.Resources ?? [])
        {
            Collect((resource.Environment ?? []).Select(e => (e.Key, e.Value)).Append(("", resource.ConnectionString)));
        }

        string? Text(string? text) => Secrets.MaskText(text, known);

        void MaskErrors(IEnumerable<ProbeError>? errors)
        {
            foreach (var error in errors ?? [])
            {
                error.Message = Text(error.Message)!;
                error.Detail = Text(error.Detail);
                error.Hint = Text(error.Hint);
            }
        }

        foreach (var service in document.Deployment.Services)
        {
            MaskAll(service.Environment, Secrets.Mask);
            service.ConnectionString = Secrets.MaskConnectionString(service.ConnectionString);
            service.Command = Text(service.Command);
        }

        document.Deployment.Notes = [.. document.Deployment.Notes.Select(n => Text(n)!)];

        foreach (var host in document.Hosts)
        {
            MaskAll(host.Environment, (key, value) => Secrets.Mask(key, value)!);
            foreach (var file in host.Configuration?.Values ?? Enumerable.Empty<SortedDictionary<string, string?>>())
            {
                MaskAll(file, Secrets.Mask);
            }

            host.BuildError = Text(host.BuildError);
            MaskErrors(host.Runtime?.Errors);
        }

        foreach (var collision in document.Collisions.ConfigurationKeys)
        {
            foreach (var site in collision.Values)
            {
                site.Value = Secrets.Mask(collision.Key, site.Value);
            }
        }

        foreach (var resource in document.Aspire?.Resources ?? [])
        {
            MaskAll(resource.Environment, Secrets.Mask);
            resource.ConnectionString = Secrets.MaskConnectionString(resource.ConnectionString);
            resource.Command = Text(resource.Command);
            MaskErrors(resource.Errors);
        }

        foreach (var error in document.Errors)
        {
            error.Message = Text(error.Message)!;
        }
    }

    // --- Projects -----------------------------------------------------------------------------

    private async Task EvaluateProjectsAsync(CancellationToken cancellationToken)
    {
        var roots = input.Solution is not null
            ? await SolutionReader.ProjectsAsync(input.Solution, cancellationToken)
            : [input.Project!];
        log($"Evaluating {roots.Count} project(s)");

        var inSolution = roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = roots.Where(File.Exists).ToList();
        foreach (var missing in roots.Where(p => !File.Exists(p)))
        {
            Error("evaluate", Relative(missing), "The solution lists a project that does not exist.");
        }

        using var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));
        while (pending.Count > 0)
        {
            var evaluated = await Task.WhenAll(pending.Select(async path =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await ProjectEvaluator.EvaluateAsync(path, workspace.GlobalProperties, options.Framework, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var project in evaluated)
            {
                project.InSolution = inSolution.Contains(project.FullPath);
                projects[project.FullPath] = project;
                if (project.Error is not null)
                {
                    Error("evaluate", Relative(project.FullPath), project.Error);
                }
            }

            // Referenced from the solution but not in it: evaluated too, so the graph is complete.
            pending = [.. evaluated
                .SelectMany(p => p.ProjectReferencePaths)
                .Where(p => !projects.ContainsKey(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        foreach (var project in projects.Values)
        {
            project.Path = Relative(project.FullPath);
            project.ProjectReferences = [.. project.ProjectReferencePaths.Select(Relative)];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(project.ProjectReferencePaths);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (!seen.Add(next))
                {
                    continue;
                }

                if (projects.TryGetValue(next, out var referenced))
                {
                    foreach (var reference in referenced.ProjectReferencePaths)
                    {
                        stack.Push(reference);
                    }
                }
            }

            project.TransitiveProjectReferences = [.. seen.Select(Relative).Order(StringComparer.OrdinalIgnoreCase)];
        }

        document.Projects = [.. projects.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)];
    }

    // --- Deployment ---------------------------------------------------------------------------

    private async Task DiscoverDeploymentAsync(CancellationToken cancellationToken)
    {
        var deployment = document.Deployment;

        var explicitCompose = options.ComposeFiles.Count > 0 ? new ComposeFiles([.. options.ComposeFiles.Select(Path.GetFullPath)], []) : null;
        var compose = explicitCompose ?? ComposeDiscovery.Discover(input.Root);

        if (input.Project is not null)
        {
            var project = projects[input.Project];
            if (project.Kind == "aspire-host")
            {
                // Read, or unreadable with compose files to fall back on.
                if (!await ReadAspireAsync([project], compose, cancellationToken) && !await ReadComposeAsync(compose!, cancellationToken))
                {
                    deployment.Kind = "none";
                    deployment.Notes.Add("The compose files map none of their services to a project of this solution either.");
                }

                return;
            }

            deployment.Kind = "project";
            deployment.Sources = [project.Path];
            deployment.ReadBy = "msbuild";
            if (HostKinds.Contains(project.Kind))
            {
                deployment.Services.Add(ExecutableService(project));
            }
            else
            {
                deployment.Notes.Add($"{project.Name} is a {project.Kind} project: static information only, nothing to probe.");
            }

            return;
        }

        var appHosts = projects.Values.Where(p => p.InSolution && p.Kind == "aspire-host").ToList();
        string? why = null;
        if (explicitCompose is not null)
        {
            // Asked for by name: the compose files are the deployment, AppHost or not.
            if (appHosts.Count > 0)
            {
                deployment.Notes.Add($"--compose-file names the deployment: the compose files are read, not the AppHost {string.Join(", ", appHosts.Select(a => a.Name))}.");
            }

            if (await ReadComposeAsync(explicitCompose, cancellationToken))
            {
                return;
            }

            why = "The compose files map none of their services to a project of this solution";
        }
        else
        {
            if (appHosts.Count > 0 && await ReadAspireAsync(appHosts, compose, cancellationToken))
            {
                return;
            }

            if (compose is not null)
            {
                if (await ReadComposeAsync(compose, cancellationToken))
                {
                    return;
                }

                why = "The compose files map none of their services to a project of this solution";
            }
        }

        var executables = projects.Values.Where(p => p.InSolution && HostKinds.Contains(p.Kind)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        deployment.ReadBy = "msbuild";
        if (executables.Count == 0)
        {
            deployment.Kind = "none";
            deployment.Notes.Add(why is null
                ? "No Aspire AppHost, no compose file and no executable project that is not a test: nothing is deployed from this solution."
                : $"{why}, and it has no executable project that is not a test: nothing is deployed from it.");
            return;
        }

        deployment.Kind = "executables";
        deployment.Sources = [.. executables.Select(p => p.Path)];
        if (why is not null)
        {
            deployment.Notes.Add($"{why} — they name images only, built elsewhere, and no service or image name matches a project — so each of the {executables.Count} executable projects that are not tests is taken to be a service of its own instead, run with its launch profile.");
        }
        else if (executables.Count > 1)
        {
            deployment.Notes.Add($"No Aspire AppHost and no compose file: each of the {executables.Count} executable projects that are not tests is taken to be a service of its own, run with its launch profile.");
        }

        deployment.Services.AddRange(executables.Select(ExecutableService));
        var clients = projects.Values.Where(p => p.InSolution && p.Kind == "client").Select(p => p.Name).ToList();
        if (clients.Count > 0)
        {
            deployment.Notes.Add($"Client applications, not services: {string.Join(", ", clients)}.");
        }
    }

    private DeploymentService ExecutableService(ProjectInfo project)
    {
        var profiles = LaunchSettingsReader.Read(Path.GetDirectoryName(project.FullPath)!);
        var profile = options.LaunchProfile is null ? profiles.FirstOrDefault() : profiles.FirstOrDefault(p => p.Name == options.LaunchProfile);
        return new DeploymentService
        {
            Name = project.Name,
            Kind = "project",
            Type = project.Kind,
            Project = project.Path,
            LaunchProfile = profile?.Name,
            Environment = profile is null || profile.Environment.Count == 0 ? null : new SortedDictionary<string, string?>(profile.Environment.ToDictionary(e => e.Key, e => (string?)e.Value), StringComparer.Ordinal),
            Ports = ListenUrls.Split(profile?.ApplicationUrl)
                .Select(ListenUrls.Parse)
                .OfType<Uri>()
                .Select(uri => new PortInfo { Scheme = uri.Scheme, Port = uri.Port })
                .ToList() is { Count: > 0 } ports ? ports : null,
        };
    }

    /// <summary>
    /// Reads the AppHost's model. When it can be neither built nor read and there are compose files
    /// to fall back on, says why and returns false: the compose files are the deployment then. Else
    /// the projects the AppHost references stand in for its services.
    /// </summary>
    private async Task<bool> ReadAspireAsync(List<ProjectInfo> appHosts, ComposeFiles? fallback, CancellationToken cancellationToken)
    {
        var deployment = document.Deployment;
        deployment.Kind = "aspire";
        var appHost = appHosts[0];
        deployment.Sources = [appHost.Path];
        if (appHosts.Count > 1)
        {
            deployment.Notes.Add($"Several AppHosts ({string.Join(", ", appHosts.Select(a => a.Name))}); this inventory reads {appHost.Name}. Run it on the others' projects for theirs.");
        }

        ProbeAspireModel? model = null;
        string? unreadable = null;
        if (!options.Static)
        {
            log($"Building and probing the AppHost {appHost.Name}");
            var build = await HostBuilder.BuildAsync([appHost], projects, input.Root, workspace, options.Framework, cancellationToken);
            if (build[appHost.FullPath] is { Count: > 0 } errors)
            {
                Error("build", appHost.Path, string.Join("\n", errors));
                unreadable = $"could not be built ({FirstBuildError(errors)})";
            }
            else
            {
                var probe = await HostProber.ProbeAsync(appHost, appHost.Name, "apphost", AppHostEnvironment(appHost), workspace, options, cancellationToken);
                foreach (var error in probe.Errors)
                {
                    Error($"apphost-{error.Stage}", appHost.Path, error.Detail is null ? error.Message : $"{error.Message}\n{error.Detail}");
                }

                model = probe.Aspire;
                deployment.ReadBy = model is null ? null : "probe";

                if (model is null)
                {
                    log("The AppHost probe read no model; trying the Aspire manifest publisher");
                    (model, var failure) = await AspireModel.FromManifestAsync(appHost, workspace, cancellationToken);
                    deployment.ReadBy = model is null ? null : "manifest";
                    unreadable = model is null ? $"could not be read ({Reason(probe).Split('\n')[0]}; {failure})" : null;
                }
            }
        }

        if (model is null && unreadable is not null && fallback is not null)
        {
            var files = string.Join(", ", fallback.Files.Select(Relative));
            log($"The AppHost {appHost.Name} {unreadable}: reading {files} instead.");
            deployment.Sources = [];
            deployment.ReadBy = null;
            deployment.Notes.Add($"The AppHost {appHost.Path} {unreadable}: the compose files ({files}) are read instead.");
            return false;
        }

        if (model is null)
        {
            // Nothing but the build graph: the projects the AppHost references are its services.
            deployment.ReadBy = "project-references";
            if (unreadable is not null)
            {
                log($"The AppHost {appHost.Name} {unreadable}: its project references stand in for its services.");
            }

            deployment.Notes.Add(unreadable is null
                ? "The AppHost's model could not be read; its services are the projects it references, without environment, references or endpoints."
                : $"The AppHost {appHost.Path} {unreadable}; its services are the projects it references, without environment, references or endpoints.");
            foreach (var reference in appHost.ProjectReferencePaths.Where(projects.ContainsKey).Select(p => projects[p]).Where(p => HostKinds.Contains(p.Kind)))
            {
                deployment.Services.Add(new DeploymentService { Name = reference.Name, Kind = "project", Type = reference.Kind, Project = reference.Path });
            }

            return true;
        }

        document.Aspire = model;
        deployment.Services.AddRange(AspireModel.Services(model, Relative));
        var tooling = model.Resources.Where(AspireModel.IsTooling).Select(r => r.Name).ToList();
        if (tooling.Count > 0)
        {
            deployment.Notes.Add($"{tooling.Count} resource(s) the AppHost adds for its own development loop are left out of the services (still under \"aspire\"): {string.Join(", ", tooling)}.");
        }

        return true;
    }

    /// <summary>"NETSDK1228: message" out of an MSBuild error line, the first that has a code.</summary>
    internal static string FirstBuildError(IReadOnlyList<string> errors)
    {
        foreach (var line in errors)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\berror\s+([A-Za-z]+\d+)\s*:\s*(.*?)(?:\s+\[[^\[\]]*\])?\s*$");
            if (match.Success)
            {
                return $"{match.Groups[1].Value}: {match.Groups[2].Value}";
            }
        }

        return errors.Count > 0 ? errors[0] : "no error reported";
    }

    /// <summary>
    /// Reads the compose files into the deployment. Returns false, and leaves no services, when not
    /// one of them is tied to a project of the solution: nothing to probe from them.
    /// </summary>
    private async Task<bool> ReadComposeAsync(ComposeFiles compose, CancellationToken cancellationToken)
    {
        var deployment = document.Deployment;
        deployment.Kind = "compose";
        deployment.Sources = [.. compose.Files.Select(Relative)];
        if (compose.Others.Count > 0)
        {
            deployment.Notes.Add($"Other compose files beside these, not merged: {string.Join(", ", compose.Others.Select(Relative))}. Pass --compose-file to choose.");
        }

        var (services, readBy, warning) = await ComposeReader.ReadAsync(compose.Files, options.UseDockerCli, cancellationToken);
        deployment.ReadBy = readBy;
        if (warning is not null)
        {
            deployment.Notes.Add(warning);
        }

        var candidates = projects.Values.ToList();
        var executables = projects.Values.Where(p => HostKinds.Contains(p.Kind)).ToList();
        foreach (var service in services.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var deploymentService = new DeploymentService
            {
                Name = service.Name,
                Image = service.Image,
                Command = service.Command,
                Replicas = service.Replicas,
                Environment = service.Environment.Count > 0 ? service.Environment : null,
                Ports = service.Ports.Count > 0 ? service.Ports : null,
                References = service.DependsOn.Count > 0 ? [.. service.DependsOn.Select(d => new ResourceReference { Name = d, Type = "depends_on" })] : null,
            };

            if (service.BuildContext is not null || service.Dockerfile is not null)
            {
                var (project, mappedBy, evidence) = DockerfileReader.Map(service.Dockerfile, candidates);
                deploymentService.Build = new BuildInfo
                {
                    Context = service.BuildContext is null ? null : Relative(service.BuildContext),
                    Dockerfile = service.Dockerfile is null ? null : Relative(service.Dockerfile),
                    MappedBy = mappedBy,
                    Evidence = evidence,
                };
                deploymentService.Kind = project is null ? "container" : "project";
                deploymentService.Type = project?.Kind ?? Infrastructure.Classify(service.Image);
                deploymentService.Project = project?.Path;
                if (project is null)
                {
                    deployment.Notes.Add($"Service '{service.Name}' builds an image that no project in the solution could be matched to ({evidence ?? "no Dockerfile"}).");
                }
            }
            else if (ImageMapper.Map(service.Name, service.Image, executables) is var mapped && mapped.Project is { } project)
            {
                // An image built elsewhere (by CI), named after its project.
                deploymentService.Kind = "project";
                deploymentService.Type = project.Kind;
                deploymentService.Project = project.Path;
                deploymentService.MappedBy = mapped.MappedBy;
                deploymentService.Evidence = mapped.Evidence;
            }
            else
            {
                // A known image (postgres, rabbitmq) is infrastructure; any other is a container of
                // unknown provenance — someone else's service, or one of these built under another name.
                deploymentService.Type = Infrastructure.Classify(service.Image, service.Name);
                deploymentService.Kind = deploymentService.Type == "container" ? "container" : "infrastructure";
                if (mapped.Evidence is { } ambiguous)
                {
                    deployment.Notes.Add($"Service '{service.Name}' builds nothing, and its name is ambiguous: {ambiguous}.");
                }
            }

            deployment.Services.Add(deploymentService);
        }

        if (deployment.Services.Any(s => s.Kind == "project"))
        {
            var unmapped = deployment.Services.Where(s => s.Kind == "container" && s.Build is null).Select(s => s.Name).ToList();
            if (unmapped.Count > 0 && deployment.Services.Any(s => s.MappedBy is not null))
            {
                deployment.Notes.Add($"Services that build nothing and whose name and image match no project, taken to be containers and not probed: {string.Join(", ", unmapped)}.");
            }

            return true;
        }

        deployment.Services.Clear();
        return false;
    }

    // --- Hosts --------------------------------------------------------------------------------

    private async Task InventoryHostsAsync(CancellationToken cancellationToken)
    {
        var units = document.Deployment.Services
            .Where(s => s.Kind == "project" && s.Project is not null)
            .Select(s => (Service: s, Project: projects.GetValueOrDefault(Path.GetFullPath(Path.Combine(input.Root, s.Project!)))))
            .Where(u => u.Project is not null && HostKinds.Contains(u.Project.Kind))
            .ToList();

        foreach (var unit in document.Deployment.Services.Where(s => s.Kind == "project" && s.Project is not null && !units.Any(u => u.Service == s)))
        {
            document.Deployment.Notes.Add($"'{unit.Name}' runs {unit.Project}, which is not an executable project of this solution: not probed.");
        }

        var hosts = units.Select(u => (u.Service, Project: u.Project!, Host: new HostInfo
        {
            Service = u.Service.Name,
            Project = u.Project!.Path,
            TargetFramework = Frameworks.Choose(u.Project.TargetFrameworks, options.Framework),
            Configuration = AppSettingsReader.Read(Path.GetDirectoryName(u.Project.FullPath)!),
            LaunchProfiles = LaunchSettingsReader.Read(Path.GetDirectoryName(u.Project.FullPath)!).Select(p => p.Name).ToList() is { Count: > 0 } names ? names : null,
        })).ToList();
        document.Hosts = [.. hosts.Select(h => h.Host)];

        ResolveScopes(hosts.Select(h => (h.Service, h.Project)));
        foreach (var (service, project, host) in hosts)
        {
            var (environment, faked) = ProbeEnvironment(service, project);
            host.Environment = new SortedDictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
            host.FakedEnvironment = faked.Count > 0 ? faked : null;
        }

        if (options.Static || hosts.Count == 0)
        {
            return;
        }

        var distinctProjects = hosts.Select(h => h.Project).Distinct().ToList();
        log($"Building {distinctProjects.Count} host project(s)");
        var build = await HostBuilder.BuildAsync(distinctProjects, projects, input.Root, workspace, options.Framework, cancellationToken);

        log($"Probing {hosts.Count} host(s), {options.Parallelism} at a time");
        using var gate = new SemaphoreSlim(options.Parallelism);
        await Task.WhenAll(hosts.Select(async h =>
        {
            if (build[h.Project.FullPath] is { Count: > 0 } errors)
            {
                h.Host.BuildError = string.Join("\n", errors);
                Error("build", h.Host.Service, h.Host.BuildError);
                log($"  {h.Host.Service}: not built, not probed\n    {FirstBuildError(errors)}");
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                h.Host.Runtime = await ProbeWithFakesAsync(h.Project, h.Host, cancellationToken);
                log(ProgressLine(h.Host.Service, h.Host.Runtime));
                if (h.Host.Runtime.Status is not "started")
                {
                    Error("probe", h.Host.Service, $"{h.Host.Runtime.Status}: {Reason(h.Host.Runtime)}");
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        if (MigrationRace(hosts.Select(h => h.Host), options.Parallelism) is { } race)
        {
            log(race);
            document.Deployment.Notes.Add(race);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SchemaFailure = new(
        @"(?i)\b(relation|table|object|column|schema)\b[^\n]*\b(does not exist|already exists)\b|invalid object name|there is already an object named|__EFMigrationsHistory|\b42P0[17]\b|duplicate key|deadlock",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Several hosts that did not start, each over the database schema — a table missing or already
    /// there — while probed in parallel: what hosts sharing one empty database do when they all run
    /// their migrations at once. Says so, and how to avoid it.
    /// </summary>
    internal static string? MigrationRace(IEnumerable<HostInfo> hosts, int parallelism)
    {
        if (parallelism <= 1)
        {
            return null;
        }

        var racing = hosts
            .Where(h => h.Runtime is { Status: not "started" } runtime
                        && runtime.Errors.Any(e => e.Stage != "output" && SchemaFailure.IsMatch($"{e.Message}\n{e.Detail}")))
            .Select(h => h.Service)
            .ToList();
        return racing.Count < 2
            ? null
            : $"{racing.Count} hosts ({string.Join(", ", racing)}) stopped over the database schema — a table missing or already there — probed {parallelism} at a time. "
              + "Hosts that share a database race on their startup migrations when they start together: run again with --parallel 1.";
    }

    /// <summary>What went wrong for a host that did not start: its first error, with the hint the probe had for it.</summary>
    internal static string Reason(ProbeOutput runtime)
    {
        var first = FirstError(runtime);
        if (first is null)
        {
            return "no error was reported";
        }

        var reason = first.Type is null ? first.Message : $"{first.Type}: {first.Message}";
        return first.Hint is null ? reason : $"{reason}\n{first.Hint}";
    }

    private static ProbeError? FirstError(ProbeOutput runtime) =>
        runtime.Errors.FirstOrDefault(e => e.Stage is not ("output" or "endpoints")) ?? runtime.Errors.FirstOrDefault();

    /// <summary>One line of progress per host: how far it got, and for one that did not start, why.</summary>
    internal static string ProgressLine(string service, ProbeOutput runtime)
    {
        var services = runtime.Services is null ? "" : $", {runtime.Services.Count} application services";
        var endpoints = runtime.Endpoints is null
            ? (runtime.Status is "skipped" or "failed" || runtime.Services is null ? "" : ", endpoints not read")
            : runtime.EndpointsReadFrom is null or "pipeline"
                ? $", {runtime.Endpoints.Count} endpoints"
                : $", {runtime.Endpoints.Count} endpoints (read from {runtime.EndpointsReadFrom})";
        var rolledForward = runtime.RolledForwardFrom is null ? "" : $", {runtime.RolledForwardFrom} rolled forward onto {runtime.Runtime}";
        var line = $"  {service}: {runtime.Status}{services}{endpoints}{rolledForward} ({runtime.DurationMs} ms)";
        if (runtime.Status == "started")
        {
            return line;
        }

        // The reason's first line, and the hint, a line each.
        var first = FirstError(runtime);
        var message = first is null ? "no error was reported" : (first.Type is null ? "" : first.Type + ": ") + first.Message.Split('\n')[0].Trim();
        string[] reason = first?.Hint is null ? [message] : [message, first.Hint];
        return string.Join("\n    ", [line, .. reason.Select(l => l.Length > 400 ? l[..400] + "…" : l)]);
    }

    /// <summary>
    /// Probes, and when the host failed for want of a connection string that nothing in the
    /// deployment supplied, probes again with a fake one — up to three times.
    /// </summary>
    private async Task<ProbeOutput> ProbeWithFakesAsync(ProjectInfo project, HostInfo host, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(host.Environment!, StringComparer.OrdinalIgnoreCase);
        var faked = host.FakedEnvironment?.ToList() ?? [];
        ProbeOutput probe;
        var attempt = 0;
        while (true)
        {
            probe = await HostProber.ProbeAsync(project, host.Service, "service", environment, workspace, options, cancellationToken);
            if (probe.Status == "started" || ++attempt > 3 || FakeConfiguration.MissingConnectionString(probe.Errors) is not { } missing)
            {
                break;
            }

            var variable = $"ConnectionStrings__{missing.Name}";
            if (environment.ContainsKey(variable))
            {
                break;
            }

            environment[variable] = FakeConfiguration.ConnectionString(missing.Kind, missing.Name);
            faked.Add(variable);
        }

        host.Environment = new SortedDictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        host.FakedEnvironment = faked.Count > 0 ? faked : null;
        return probe;
    }

    private (Dictionary<string, string> Environment, List<string> Faked) ProbeEnvironment(DeploymentService service, ProjectInfo project)
    {
        Dictionary<string, string> environment;
        List<string> faked;
        if (document.Aspire is not null && document.Aspire.Resources.FirstOrDefault(r => r.Name == service.Name) is { } resource)
        {
            (environment, faked) = AspireModel.ProbeEnvironment(resource, document.Aspire);
        }
        else
        {
            environment = (service.Environment ?? []).Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.OrdinalIgnoreCase);
            faked = [];
        }

        foreach (var (name, value) in BaseEnvironment(environment, [service.Name, project.Name, project.AssemblyName]))
        {
            environment[name] = value;
        }

        return (environment, faked);
    }

    // --env host:NAME=VALUE whose host is none of the run's: taken to be a plain NAME with a colon.
    private HashSet<string> unscoped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scoped --env entries whose scope names no host of this run — by service, project or
    /// assembly name — are a variable whose name holds a colon (ConnectionStrings:db), for every host.
    /// </summary>
    private void ResolveScopes(IEnumerable<(DeploymentService Service, ProjectInfo Project)> hosts)
    {
        var names = hosts.SelectMany(h => new[] { h.Service.Name, h.Project.Name, h.Project.AssemblyName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        unscoped = [.. options.ScopedEnvironment.Where(e => !names.Contains(e.Scope)).Select(e => e.Scope)];
        foreach (var scope in unscoped)
        {
            log($"--env {scope}:…: no host is called {scope}; passed to every host as a variable named {scope}:….");
        }
    }

    /// <summary>
    /// The AppHost's environment. It is probed before the hosts are known — they are what its model
    /// says — so a scoped --env entry is decided against the projects instead: for the AppHost by
    /// its name, for a project of the solution not (a host of the run, then), and otherwise a
    /// variable whose name holds a colon (Parameters:pg-password=…), as for every host.
    /// </summary>
    private Dictionary<string, string> AppHostEnvironment(ProjectInfo appHost)
    {
        var projectNames = projects.Values.SelectMany(p => new[] { p.Name, p.AssemblyName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hostNames = new[] { appHost.Name, appHost.AssemblyName };
        var environment = BaseEnvironment(null, hostNames);
        foreach (var (scope, key, value) in options.ScopedEnvironment)
        {
            if (!projectNames.Contains(scope))
            {
                environment[$"{scope}:{key}"] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// The environment name and the user's --env on top of what the deployment says: the plain ones
    /// for every host, host:NAME=VALUE for the host of that service, project or assembly name only.
    /// </summary>
    private Dictionary<string, string> BaseEnvironment(IReadOnlyDictionary<string, string>? deployment, IReadOnlyCollection<string?>? hostNames = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = options.EnvironmentName
                   ?? deployment?.GetValueOrDefault("ASPNETCORE_ENVIRONMENT")
                   ?? deployment?.GetValueOrDefault("DOTNET_ENVIRONMENT")
                   ?? "Development";
        environment["ASPNETCORE_ENVIRONMENT"] = name;
        environment["DOTNET_ENVIRONMENT"] = name;
        foreach (var (key, value) in options.Environment)
        {
            environment[key] = value;
        }

        foreach (var (scope, key, value) in options.ScopedEnvironment)
        {
            if (unscoped.Contains(scope))
            {
                environment[$"{scope}:{key}"] = value;
            }
            else if (hostNames?.Any(n => string.Equals(n, scope, StringComparison.OrdinalIgnoreCase)) == true)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private void Error(string stage, string? subject, string message)
    {
        lock (document.Errors)
        {
            document.Errors.Add(new InventoryError { Stage = stage, Subject = subject, Message = message });
        }
    }

    private string Relative(string path) => Path.GetRelativePath(input.Root, path).Replace('\\', '/');
}
