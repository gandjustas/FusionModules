using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

// The output of `dotnet modules inventory`. docs/inventory.md describes it field by field; keep the
// two in step. Paths are relative to Root with forward slashes unless they lie outside it.

internal sealed class InventoryDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string Tool { get; set; } = "";

    public DateTimeOffset GeneratedAtUtc { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Absolute: the directory of the solution or project. Every other path is relative to it.</summary>
    public string Root { get; set; } = "";

    public InputInfo Input { get; set; } = new();

    public DeploymentInfo Deployment { get; set; } = new();

    public List<ProjectInfo> Projects { get; set; } = [];

    public List<HostInfo> Hosts { get; set; } = [];

    /// <summary>The AppHost's DistributedApplicationModel as the probe read it, when the deployment is Aspire.</summary>
    public ProbeAspireModel? Aspire { get; set; }

    public CollisionsInfo Collisions { get; set; } = new();

    public List<InventoryError> Errors { get; set; } = [];
}

internal sealed class InputInfo
{
    public string Path { get; set; } = "";

    /// <summary>solution | project</summary>
    public string Kind { get; set; } = "";

    public string? Solution { get; set; }

    public string? Project { get; set; }
}

internal sealed class DeploymentInfo
{
    /// <summary>aspire | compose | executables | project | none</summary>
    public string Kind { get; set; } = "none";

    /// <summary>What it was read from: the AppHost project, the compose files, the executable projects.</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>How it was read: probe | manifest | project-references | docker-compose-config | yaml | msbuild.</summary>
    public string? ReadBy { get; set; }

    public List<string> Notes { get; set; } = [];

    public List<DeploymentService> Services { get; set; } = [];
}

internal sealed class DeploymentService
{
    public string Name { get; set; } = "";

    /// <summary>project | container | executable | parameter | infrastructure | other</summary>
    public string Kind { get; set; } = "other";

    /// <summary>Aspire resource type (ProjectResource, PostgresDatabaseResource…), or what a compose image is (postgres, rabbitmq…).</summary>
    public string? Type { get; set; }

    public string? Project { get; set; }

    public string? Image { get; set; }

    public BuildInfo? Build { get; set; }

    /// <summary>For a compose service that builds nothing: how its image was tied to a project — service-name | image-name.</summary>
    public string? MappedBy { get; set; }

    /// <summary>What matched, for MappedBy: "catalog-api matches project Catalog.Api".</summary>
    public string? Evidence { get; set; }

    public string? Command { get; set; }

    public string? Parent { get; set; }

    public string? ConnectionString { get; set; }

    public string? LaunchProfile { get; set; }

    public int? Replicas { get; set; }

    public SortedDictionary<string, string?>? Environment { get; set; }

    public List<ResourceReference>? References { get; set; }

    public List<PortInfo>? Ports { get; set; }
}

internal sealed class BuildInfo
{
    public string? Context { get; set; }

    public string? Dockerfile { get; set; }

    /// <summary>How the image was tied to a project: entrypoint | publish | dockerfile-directory | none.</summary>
    public string? MappedBy { get; set; }

    public string? Evidence { get; set; }
}

internal sealed class ResourceReference
{
    public string Name { get; set; } = "";

    /// <summary>Reference | WaitFor | Parent | depends_on | environment</summary>
    public string Type { get; set; } = "";
}

internal sealed class PortInfo
{
    public string? Name { get; set; }

    public string? Scheme { get; set; }

    public string? Protocol { get; set; }

    public int? Port { get; set; }

    public int? TargetPort { get; set; }

    public bool? External { get; set; }
}

internal sealed class ProjectInfo
{
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public string? AssemblyName { get; set; }

    public string? Sdk { get; set; }

    /// <summary>aspire-host | web | worker | console | client | test | library</summary>
    public string Kind { get; set; } = "library";

    public string? OutputType { get; set; }

    public List<string> TargetFrameworks { get; set; } = [];

    public bool IsTestProject { get; set; }

    public bool InSolution { get; set; }

    public List<string> ProjectReferences { get; set; } = [];

    public List<string> TransitiveProjectReferences { get; set; } = [];

    public List<PackageInfo> PackageReferences { get; set; } = [];

    public List<string>? FrameworkReferences { get; set; }

    public SortedDictionary<string, string>? Properties { get; set; }

    public string? Error { get; set; }

    internal string FullPath { get; set; } = "";

    internal string? TargetPath { get; set; }

    internal string? ProjectAssetsFile { get; set; }

    internal List<string> ProjectReferencePaths { get; set; } = [];
}

internal sealed class PackageInfo
{
    public string Id { get; set; } = "";

    /// <summary>The version the project asks for: Version, VersionOverride, or the central PackageVersion.</summary>
    public string? Requested { get; set; }

    /// <summary>From the restore's project.assets.json, where there is one.</summary>
    public string? Resolved { get; set; }

    public bool? Implicit { get; set; }
}

internal sealed class HostInfo
{
    /// <summary>The deployment service this host runs as.</summary>
    public string Service { get; set; } = "";

    public string Project { get; set; } = "";

    public string? TargetFramework { get; set; }

    /// <summary>What the probe ran with on top of the inherited environment: the service's own, plus fakes.</summary>
    public SortedDictionary<string, string>? Environment { get; set; }

    /// <summary>The names in Environment whose values were invented rather than read from the deployment.</summary>
    public List<string>? FakedEnvironment { get; set; }

    public List<string>? LaunchProfiles { get; set; }

    /// <summary>appsettings*.json in the project directory, flattened to configuration keys.</summary>
    public SortedDictionary<string, SortedDictionary<string, string?>>? Configuration { get; set; }

    public string? BuildError { get; set; }

    public ProbeOutput? Runtime { get; set; }
}

internal sealed class CollisionsInfo
{
    public List<ServiceCollision> Services { get; set; } = [];

    public List<RouteCollision> Routes { get; set; } = [];

    public List<NameCollision> EndpointNames { get; set; } = [];

    public List<NameCollision> TypeNames { get; set; } = [];

    public List<ConfigurationCollision> ConfigurationKeys { get; set; } = [];

    public List<DbContextCollision> DbContexts { get; set; } = [];
}

internal sealed class RouteCollision
{
    public string Route { get; set; } = "";

    /// <summary>
    /// Every site runs the same handler — a health check or an OpenAPI document mapped by a shared
    /// library. Still one route served twice once merged, but one that is usually meant to be
    /// mapped once by the host rather than resolved.
    /// </summary>
    public bool SharedHandler { get; set; }

    public List<RouteSite> Sites { get; set; } = [];
}

internal sealed class RouteSite
{
    public string Service { get; set; } = "";

    public string? Route { get; set; }

    public List<string>? Methods { get; set; }

    public string? Handler { get; set; }

    public string? Assembly { get; set; }

    public List<string>? Authorization { get; set; }
}

internal sealed class ConfigurationCollision
{
    public string Key { get; set; } = "";

    /// <summary>The layer compared: a file name such as appsettings.json, or "environment".</summary>
    public string Source { get; set; } = "";

    public List<ConfigurationSite> Values { get; set; } = [];
}

internal sealed class ConfigurationSite
{
    public string Service { get; set; } = "";

    public string? Value { get; set; }
}

internal sealed class ServiceCollision
{
    /// <summary>The service type, with its key.</summary>
    public string ServiceType { get; set; } = "";

    /// <summary>
    /// True when modules register it, each site a module: the startup check fails. False when the
    /// programs do: the composed host registers it once, from one program, so it is reconciled there.
    /// </summary>
    public bool ByModules { get; set; }

    public List<ServiceSite> Sites { get; set; } = [];
}

internal sealed class ServiceSite
{
    public string Service { get; set; } = "";

    /// <summary>The module that registers it; the service's own name for a service with no module yet; null for its program.</summary>
    public string? Module { get; set; }

    /// <summary>Each a type or a factory's method, with its assembly.</summary>
    public List<string> Implementations { get; set; } = [];
}

internal sealed class NameCollision
{
    public string Name { get; set; } = "";

    public List<NameSite> Sites { get; set; } = [];
}

internal sealed class NameSite
{
    public string Service { get; set; } = "";

    /// <summary>The endpoint that has the name, or the assembly that has the type.</summary>
    public string Where { get; set; } = "";
}

internal sealed class DbContextCollision
{
    public string Type { get; set; } = "";

    public List<DbContextSite> Sites { get; set; } = [];
}

internal sealed class DbContextSite
{
    public string Service { get; set; } = "";

    public string? Provider { get; set; }

    public string? ConnectionString { get; set; }
}

internal sealed class InventoryError
{
    public string Stage { get; set; } = "";

    public string? Subject { get; set; }

    public string Message { get; set; } = "";
}
