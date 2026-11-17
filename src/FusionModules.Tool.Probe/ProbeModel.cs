// What the probe writes and the tool reads back. Compiled into both projects (the tool links this
// file), so the two cannot disagree about the shape; the tool also writes these types into its own
// output unchanged, under "hosts[].runtime" and "aspire".

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionModules.Tool.Probe;

/// <summary>The one JSON format: of the probe's result, as the probe writes it and the tool reads it, and of the tool's document.</summary>
internal static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed class ProbeOutput
{
    /// <summary>service | apphost</summary>
    public string Mode { get; set; } = "service";

    /// <summary>
    /// started: the host got as far as IServer.StartAsync (or ApplicationStarted for a worker), so the
    /// endpoints are complete. built: the container was built but startup did not complete — see
    /// errors. failed: not even that. timeout: the host did not start in time.
    /// </summary>
    public string Status { get; set; } = "failed";

    /// <summary>The runtime the probe ran in, as RuntimeInformation.FrameworkDescription says.</summary>
    public string? Runtime { get; set; }

    /// <summary>
    /// The application's own target framework, when it is older than the probe's (net5.0 to net7.0):
    /// the probe then ran it rolled forward onto the newest runtime installed, named in Runtime.
    /// </summary>
    public string? RolledForwardFrom { get; set; }

    public long DurationMs { get; set; }

    public string? EnvironmentName { get; set; }

    public List<string>? HostingStartupAssemblies { get; set; }

    /// <summary>The project-type libraries of the application's deps.json: what "application" means below.</summary>
    public List<string>? ApplicationAssemblies { get; set; }

    public int? TotalServices { get; set; }

    public List<ProbeService>? Services { get; set; }

    /// <summary>Registrations that touch no application type, counted by the assembly of the service type.</summary>
    public SortedDictionary<string, int>? FrameworkServices { get; set; }

    /// <summary>
    /// The FusionModules modules the host activated: each registers itself as an IStartupFilter.
    /// Wiring of FusionModules', not the application's, so not under Services.
    /// </summary>
    public List<ProbeModule>? Modules { get; set; }

    public List<ProbeHostedService>? HostedServices { get; set; }

    public List<ProbeDbContext>? DbContexts { get; set; }

    /// <summary>
    /// null when the endpoints were not read — not a web host, or it stopped before they could be;
    /// errors say why. An empty list is a web host that maps nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<ProbeEndpoint>? Endpoints { get; set; }

    /// <summary>
    /// Where the endpoints were read: pipeline (the built request pipeline: complete); application
    /// (the WebApplication's own route builder before its pipeline was built: what Program.cs mapped
    /// before it stopped); action-descriptors (MVC's controllers and pages, never mapped).
    /// </summary>
    public string? EndpointsReadFrom { get; set; }

    /// <summary>How many endpoints MapStaticAssets added: one entry of kind static-assets stands for them all.</summary>
    public int? StaticAssetEndpoints { get; set; }

    public ProbeAspireModel? Aspire { get; set; }

    public List<ProbeError> Errors { get; set; } = [];
}

internal sealed class ProbeService
{
    public string ServiceType { get; set; } = "";

    public string Lifetime { get; set; } = "";

    public string? Key { get; set; }

    /// <summary>type | instance | factory</summary>
    public string Implementation { get; set; } = "";

    public string? ImplementationType { get; set; }

    /// <summary>For a factory: the method the delegate points at. A lambda's declaring type is a closure inside the registering assembly.</summary>
    public string? Factory { get; set; }

    /// <summary>The assembly of the implementation type, or of the factory's declaring type.</summary>
    public string? Assembly { get; set; }

    public string? ServiceAssembly { get; set; }

    /// <summary>
    /// The module whose <c>ConfigureServices</c> made this registration, as FusionModules recorded
    /// it; null for the program's own and the framework's, and in a host without modules.
    /// </summary>
    public string? Module { get; set; }

    /// <summary>
    /// Set when the service type (and key) is registered more than once and is not one the framework
    /// resolves as a collection: true on the registration a single resolution returns — the last —
    /// false on the ones it overrides. A refactoring that flips registration order flips these.
    /// </summary>
    public bool? Wins { get; set; }
}

internal sealed class ProbeModule
{
    public string Type { get; set; } = "";

    public string? Assembly { get; set; }
}

internal sealed class ProbeHostedService
{
    public string? Type { get; set; }

    public string? Factory { get; set; }

    public string? Assembly { get; set; }

    public bool Application { get; set; }
}

internal sealed class ProbeDbContext
{
    public string Type { get; set; } = "";

    public string? Assembly { get; set; }

    public string? Lifetime { get; set; }

    public bool Pooled { get; set; }

    public string? Provider { get; set; }

    /// <summary>Passwords masked. Whatever the configuration the probe ran with said, which may be a fake.</summary>
    public string? ConnectionString { get; set; }

    public string? MigrationsAssembly { get; set; }

    public string? MigrationsHistoryTable { get; set; }

    public string? Error { get; set; }

    /// <summary>The context's model; null when it was not read — <see cref="ModelError"/> says why, when there is a why.</summary>
    public ProbeDbModel? Model { get; set; }

    public string? ModelError { get; set; }
}

/// <summary>A context's model in EF Core's own words: its debug view, line by line.</summary>
internal sealed class ProbeDbModel
{
    /// <summary>What the model says outside its entity types: a default schema, sequences.</summary>
    public List<string> Lines { get; set; } = [];

    public List<ProbeDbEntity> Entities { get; set; } = [];
}

internal sealed class ProbeDbEntity
{
    public string Name { get; set; } = "";

    /// <summary>The entity type's debug view, then its query filters and its seed data.</summary>
    public List<string> Lines { get; set; } = [];
}

internal sealed class ProbeEndpoint
{
    /// <summary>The route the endpoint answers. For a conventional route, the template with the action's values filled in.</summary>
    public string? Route { get; set; }

    /// <summary>The conventional route template the route was filled in from, when it differs.</summary>
    public string? Template { get; set; }

    public List<string>? Methods { get; set; }

    /// <summary>minimal | controller | page | component | hub | grpc | static-assets | other</summary>
    public string Kind { get; set; } = "other";

    public string? DisplayName { get; set; }

    public string? Name { get; set; }

    public string? Handler { get; set; }

    public string? Assembly { get; set; }

    public List<string>? Authorization { get; set; }

    public bool AllowAnonymous { get; set; }
}

internal sealed class ProbeError
{
    public string Stage { get; set; } = "";

    public string? Type { get; set; }

    public string Message { get; set; } = "";

    /// <summary>Where it was thrown, where the application's own code called in, and what caused it.</summary>
    public string? Detail { get; set; }

    /// <summary>What probably happened and what to do about it, when the exception says.</summary>
    public string? Hint { get; set; }

    /// <summary>
    /// The application's assemblies, which a stack trace is read for: the frames worth showing are
    /// where the application's code called into the library that threw. Set by the probe, once, for
    /// the process; <see cref="From"/> takes them as an argument too.
    /// </summary>
    public static IReadOnlySet<string>? ApplicationAssemblies { get; set; }

    public static ProbeError From(string stage, Exception exception, IReadOnlySet<string>? applicationAssemblies = null)
    {
        while (exception is System.Reflection.TargetInvocationException or AggregateException && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        var frames = StackFrames.Of(exception, applicationAssemblies ?? ApplicationAssemblies);
        var applicationFrame = frames.FirstOrDefault(f => f.Application);
        return new ProbeError
        {
            Stage = stage,
            Type = exception.GetType().FullName,
            Message = exception.Message,
            Detail = StackFrames.Detail(exception, frames),
            Hint = ErrorHints.For(stage, exception, applicationFrame?.Method),
        };
    }

    public static ProbeError Of(string stage, string message) => new() { Stage = stage, Message = message };
}

internal sealed record StackFrameInfo(string Method, string? Location, bool Application)
{
    public override string ToString() => Location is null ? $"at {Method}" : $"at {Method} in {Location}";
}

internal static class StackFrames
{
    private const int ApplicationFrames = 6;

    public static List<StackFrameInfo> Of(Exception exception, IReadOnlySet<string>? applicationAssemblies)
    {
        var result = new List<StackFrameInfo>();
        StackFrame[] frames;
        try
        {
            frames = new StackTrace(exception, fNeedFileInfo: true).GetFrames();
        }
        catch
        {
            return result;
        }

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            var type = method.DeclaringType;
            var name = method.Name;
            // An async method runs as MoveNext of a state machine nested in its type: <Name>d__3.
            if (name == "MoveNext" && type?.Name is ['<', ..] stateMachine && stateMachine.IndexOf('>') is > 1 and var end)
            {
                name = stateMachine[1..end];
                type = type.DeclaringType;
            }

            var outer = type;
            while (outer?.DeclaringType is not null && outer.Name.Contains('<'))
            {
                outer = outer.DeclaringType;
            }

            var file = frame.GetFileName();
            var assembly = outer?.Assembly.GetName().Name;
            result.Add(new StackFrameInfo(
                $"{TypeName(type)}.{name}",
                file is null ? null : $"{file}:line {frame.GetFileLineNumber()}",
                assembly is not null && applicationAssemblies?.Contains(assembly) == true));
        }

        return result;
    }

    /// <summary>
    /// The throw site, then the application's frames — the first of them is where its own code
    /// called into whatever threw — then the inner exceptions. Without application frames, the
    /// first eight frames, as a plain stack trace would show them.
    /// </summary>
    public static string? Detail(Exception exception, List<StackFrameInfo> frames)
    {
        var lines = new List<string>();
        if (frames.Count > 0)
        {
            var application = frames.Where(f => f.Application).Take(ApplicationFrames).ToList();
            if (application.Count == 0)
            {
                lines.AddRange(frames.Take(8).Select(f => f.ToString()));
            }
            else
            {
                lines.Add($"thrown {frames[0]}");
                if (!frames[0].Application)
                {
                    lines.Add("called from the application's code:");
                }

                lines.AddRange(application.Where(f => f != frames[0]).Select(f => "  " + f));
            }
        }

        var depth = 0;
        for (var inner = exception.InnerException; inner is not null && depth < 3; inner = inner.InnerException, depth++)
        {
            lines.Add($"caused by {inner.GetType().FullName}: {inner.Message}");
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string TypeName(Type? type)
    {
        if (type is null)
        {
            return "?";
        }

        var name = (type.FullName ?? type.Name).Replace('+', '.');
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}

/// <summary>What an exception most likely means for an inventory, said in a sentence.</summary>
internal static class ErrorHints
{
    /// <summary>
    /// host:port every fake address of the tool points at (FakeConfiguration.Address), passed to the
    /// probe as --fake-address: a connection failure naming it was a fake's.
    /// </summary>
    public static string FakeAddress { get; set; } = "127.0.0.1:1";

    /// <summary>The fake address as clients name it: host:port, or SQL Server's host,port.</summary>
    private static bool NamesFakeAddress(string message)
    {
        var colon = FakeAddress.LastIndexOf(':');
        var spellings = colon < 0 ? [FakeAddress] : new[] { FakeAddress, $"{FakeAddress[..colon]},{FakeAddress[(colon + 1)..]}" };
        return spellings.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  && (message.Length == message.IndexOf(s, StringComparison.OrdinalIgnoreCase) + s.Length
                                      || !char.IsDigit(message[message.IndexOf(s, StringComparison.OrdinalIgnoreCase) + s.Length])));
    }

    private static readonly (string Prefix, string Name)[] Clients =
    [
        ("Npgsql", "PostgreSQL"), ("Microsoft.Data.SqlClient", "SQL Server"), ("System.Data.SqlClient", "SQL Server"),
        ("MySqlConnector", "MySQL"), ("MySql.", "MySQL"), ("Oracle.", "Oracle"), ("MongoDB.", "MongoDB"),
        ("StackExchange.Redis", "Redis"), ("RabbitMQ.", "RabbitMQ"), ("Confluent.Kafka", "Kafka"), ("NATS.", "NATS"),
        ("Elastic.", "Elasticsearch"), ("Microsoft.Data.Sqlite", "SQLite"), ("Azure.", "an Azure service"),
        ("Microsoft.Azure.Cosmos", "Cosmos DB"), ("EventStore.", "EventStoreDB"), ("KurrentDB.", "KurrentDB"),
        ("Marten", "PostgreSQL"), ("Grpc.", "a gRPC service"), ("System.Net.Http", "an HTTP service"),
    ];

    private static readonly string[] ConnectionPhrases =
    [
        "failed to connect", "unable to connect", "could not connect", "connection refused", "actively refused",
        "no such host", "name or service not known", "nodename nor servname", "network-related", "server was not found",
        "timeout expired", "timed out", "it was not possible to connect", "none of the specified endpoints were reachable",
        "connection attempt failed", "no connection could be made", "host is unknown", "unknown host",
    ];

    public static string? For(string stage, Exception exception, string? applicationFrame)
    {
        var chain = Chain(exception).ToList();
        var where = applicationFrame is null ? "" : $" (in {applicationFrame})";

        if (chain.Any(e => (e.GetType().Namespace ?? "").StartsWith("MassTransit", StringComparison.Ordinal)
                           && e.Message.Contains("license", StringComparison.OrdinalIgnoreCase)))
        {
            return "MassTransit 9 needs a license before its bus can be created: pass --env MT_LICENSE=<license> (or MT_LICENSE_PATH). "
                   + "The application fails the same way when it runs without one.";
        }

        if (chain.FirstOrDefault(e => e is DirectoryNotFoundException or FileNotFoundException) is { } missing
            && (missing.Message.Contains(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase)
                || (missing is FileNotFoundException { FileName: { } file } && !Path.IsPathRooted(file) && file.IndexOfAny(['/', '\\']) >= 0)))
        {
            return $"The probe runs the application in a scratch working directory ({Environment.CurrentDirectory}), not in its project directory, "
                   + $"and a relative path{where} was resolved against it. Its content root is the project directory: code that reads files through "
                   + "IHostEnvironment.ContentRootPath finds them; code relative to the working directory only works when started from the project directory.";
        }

        if (Infrastructure(chain) is { } infrastructure)
        {
            var fake = chain.Any(e => NamesFakeAddress(e.Message))
                ? $" It was given the tool's fake address ({FakeAddress}, a loopback port nothing listens on): nothing in the deployment configures it."
                : "";
            return stage switch
            {
                "startup" => $"The application's code after Build() failed{where}: {infrastructure} is not running or not reachable.{fake} "
                             + "Start it, or pass --environment/--env so that this code takes another path. The registrations are complete; "
                             + "the endpoints are those mapped before it stopped.",
                _ => $"{infrastructure} is not running or not reachable{where}.{fake} Start it, or pass --environment/--env.",
            };
        }

        // eShop's WebApp in project mode: IdentityUrl and CallBackUrl come from its AppHost only.
        if (chain.FirstOrDefault(e => e.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)
                                      && (e.Message.Contains("missing", StringComparison.OrdinalIgnoreCase)
                                          || e.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                                          || e.Message.Contains("not set", StringComparison.OrdinalIgnoreCase))) is { } unset)
        {
            return $"The application needs configuration{where} that nothing here gave it: \"{unset.Message}\". A value its deployment sets — "
                   + "an Aspire AppHost, a compose file — reaches it when the inventory is run on the solution or folder; on one project, pass --env <Name>=<value>.";
        }

        if (stage == "startup")
        {
            return $"The application's code after Build() threw{where}. The registrations are complete; the endpoints are those mapped before it stopped.";
        }

        return null;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 16)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    /// <summary>The service a connection failed to reach, named by the client library that threw, when it was a connection failure.</summary>
    private static string? Infrastructure(List<Exception> chain)
    {
        var connection = chain.Any(e => e is System.Net.Sockets.SocketException or TimeoutException
                                        || e.GetType().Name is "SocketException" or "NpgsqlException" or "SqlException" or "MySqlException"
                                            or "RedisConnectionException" or "BrokerUnreachableException" or "MongoConnectionException"
                                            or "MongoAuthenticationException" or "OracleException"
                                        || ConnectionPhrases.Any(p => e.Message.Contains(p, StringComparison.OrdinalIgnoreCase)));
        if (!connection)
        {
            return null;
        }

        foreach (var exception in chain)
        {
            var name = exception.GetType().FullName ?? "";
            if (Clients.FirstOrDefault(c => name.StartsWith(c.Prefix, StringComparison.Ordinal)) is { Name: { } client })
            {
                return client;
            }
        }

        return "a service it connects to";
    }
}

internal static class StaticAssets
{
    public const string Assembly = "Microsoft.AspNetCore.StaticAssets";

    /// <summary>
    /// MapStaticAssets maps one endpoint per file and encoding — thousands in a web UI — that say
    /// nothing a migration needs one by one: one entry of kind static-assets stands for them, and
    /// the count is kept.
    /// </summary>
    public static int Collapse(List<ProbeEndpoint> endpoints)
    {
        var count = endpoints.RemoveAll(e => e.Kind == "static-assets" || (e.Kind == "other" && e.Assembly == Assembly));
        if (count > 0)
        {
            endpoints.Add(new ProbeEndpoint
            {
                Kind = "static-assets",
                DisplayName = $"{count} static asset endpoints (MapStaticAssets)",
                Assembly = Assembly,
            });
        }

        return count;
    }
}

internal static class Overrides
{
    /// <summary>
    /// Service types the framework resolves as a collection, so that registering several is the
    /// point rather than an override: options configuration, hosted services, startup filters,
    /// authorization handlers, MediatR's notification handlers and behaviours.
    /// </summary>
    private static readonly string[] CollectionPrefixes =
    [
        "Microsoft.Extensions.Options.", "Microsoft.Extensions.Hosting.IHostedService", "Microsoft.AspNetCore.Hosting.IStartupFilter",
        "Microsoft.AspNetCore.Authorization.IAuthorizationHandler", "Microsoft.Extensions.Logging.ILoggerProvider",
        "Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheckPublisher", "Microsoft.AspNetCore.Mvc.ApplicationModels.",
        "Microsoft.AspNetCore.Mvc.Filters.IFilterProvider", "Microsoft.AspNetCore.Http.IMiddleware",
        "Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter", "Microsoft.AspNetCore.SignalR.IHubFilter",
        "MediatR.INotificationHandler<", "MediatR.IPipelineBehavior<", "MediatR.IStreamPipelineBehavior<", "MediatR.Pipeline.",
    ];

    /// <summary>
    /// Marks, in registration order, which of several registrations of one service type and key a
    /// single resolution returns: the last. Types resolved as collections are left unmarked.
    /// </summary>
    public static void Mark(IReadOnlyList<ProbeService> registrations)
    {
        foreach (var group in registrations
                     .Where(r => !CollectionPrefixes.Any(p => r.ServiceType.StartsWith(p, StringComparison.Ordinal)))
                     .GroupBy(r => (r.ServiceType, r.Key))
                     .Where(g => g.Count() > 1))
        {
            var members = group.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                members[i].Wins = i == members.Count - 1;
            }
        }
    }
}

internal sealed class ProbeAspireModel
{
    public string? Version { get; set; }

    public List<ProbeAspireResource> Resources { get; set; } = [];
}

internal sealed class ProbeAspireResource
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public string? Parent { get; set; }

    public string? ProjectPath { get; set; }

    public string? Image { get; set; }

    public string? DockerfileContext { get; set; }

    public string? Dockerfile { get; set; }

    public string? Command { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? Replicas { get; set; }

    public bool? Secret { get; set; }

    public string? ConnectionString { get; set; }

    public List<ProbeAspireEndpoint>? Endpoints { get; set; }

    public List<ProbeAspireRelationship>? Relationships { get; set; }

    public SortedDictionary<string, string?>? Environment { get; set; }

    public List<string>? Annotations { get; set; }

    public List<ProbeError>? Errors { get; set; }
}

internal sealed class ProbeAspireEndpoint
{
    public string? Name { get; set; }

    public string? Scheme { get; set; }

    public string? Transport { get; set; }

    public int? Port { get; set; }

    public int? TargetPort { get; set; }

    public bool External { get; set; }
}

internal sealed class ProbeAspireRelationship
{
    public string Resource { get; set; } = "";

    public string Type { get; set; } = "";
}
