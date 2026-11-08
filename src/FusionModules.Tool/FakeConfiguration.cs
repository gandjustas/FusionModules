using System.Text.RegularExpressions;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

/// <summary>
/// Configuration that is parseable and points at nothing: never at this machine, where a real
/// database may be listening on its default port. A host registering an Aspire client
/// integration (<c>builder.AddNpgsqlDbContext("db")</c>) throws at registration when the connection
/// string is missing, before the probe has seen anything; given a well-formed one it registers and
/// never connects, because the probe never resolves the client and removes the hosted services
/// that would.
/// </summary>
internal static partial class FakeConfiguration
{
    /// <summary>
    /// Where every fake address points: loopback, on a port nothing listens on (<see cref="Port"/>).
    /// A client that connects after all is refused — no DNS involved, so neither a resolver that
    /// answers every name (the fake-IP DNS of a TUN proxy answers even <c>.invalid</c>) nor whatever
    /// listens on a default port of this machine — a developer's own database, another project's
    /// container — ever sees the connection.
    /// </summary>
    public const string Host = "127.0.0.1";

    /// <summary>The ports tried, in order, for <see cref="Port"/>: tcpmux and discard, which nothing runs nowadays.</summary>
    internal static readonly int[] PortCandidates = [1, 9];

    // Once per process: every fake of a run — and of every test in the test process — names the
    // same port, and the check is paid once.
    private static readonly Lazy<int> DeadPort = new(() => FindDeadPort());

    /// <summary>The port every fake address uses: one found refusing connections when first asked for (<see cref="FindDeadPort"/>).</summary>
    public static int Port => DeadPort.Value;

    /// <summary>host:port, as a client's error message names it.</summary>
    public static string Address => $"{Host}:{Port}";

    /// <summary>
    /// The first candidate port on loopback that nothing answers on; failing them all, a port the
    /// system just handed out and took back, which nothing has had time to listen on.
    /// </summary>
    public static int FindDeadPort(IEnumerable<int>? candidates = null)
    {
        foreach (var port in candidates ?? PortCandidates)
        {
            if (Refuses(port))
            {
                return port;
            }
        }

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var free = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return free;
    }

    /// <summary>
    /// Whether nothing answers on the port. A listener on loopback accepts within a millisecond or
    /// two, so a connection not made within a quarter of a second means there is none: a refusal,
    /// or — on Windows, which retries a refused SYN for about two seconds before it reports the
    /// refusal — those retries still going on. Either way nothing listens, and nobody waits the two
    /// seconds out.
    /// </summary>
    private static bool Refuses(int port)
    {
        using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            return !client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (AggregateException exception) when (exception.InnerException is System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// The one fake secret: every password of a fake connection string and every parameter value, so
    /// that a password variable and the connection string built from it agree. Valid base64 of 48
    /// bytes (64 bytes as UTF-8), long enough for a value used as an HMAC-SHA256 signing key; letters
    /// and digits only, so it needs no escaping in a URI or a connection string.
    /// </summary>
    public const string Secret = "ZnVzaW9ubW9kdWxlc3Byb2JlZmFrZXNlY3JldG5vdGFyZWFsa2V5YXRhbGwwMTIz";

    /// <summary>The kinds <see cref="ConnectionString"/> knows, "other" last.</summary>
    internal static readonly string[] Kinds =
    [
        "postgres", "sqlserver", "mysql", "oracle", "redis", "rabbitmq", "mongodb", "kafka", "nats", "elasticsearch", "qdrant", "milvus",
        "azure-storage", "azure-servicebus", "azure-eventhubs", "cosmosdb", "ai", "ollama", "seq", "kurrentdb", "eventstore", "other",
    ];

    // The emulators' published account keys: well-formed, and they grant nothing anywhere else.
    private const string StorageEmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string CosmosEmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    /// <summary>
    /// A connection string of the right shape for the kind, at <see cref="Host"/> and <see cref="Port"/>,
    /// with a short connect timeout where the client has one. For a resource whose own connection
    /// string expression the model has, <see cref="Resolve"/> fakes that instead: this is the guess
    /// for when there is none.
    /// </summary>
    public static string ConnectionString(string kind, string name)
    {
        var (host, port) = (Host, Port);
        return kind switch
        {
            "postgres" => $"Host={host};Port={port};Database={name};Username=postgres;Password={Secret};Timeout=2",
            "sqlserver" => $"Server={host},{port};Database={name};User ID=sa;Password={Secret};TrustServerCertificate=true;Connect Timeout=2;ConnectRetryCount=0",
            "mysql" => $"Server={host};Port={port};Database={name};User ID=root;Password={Secret};Connect Timeout=2",
            "oracle" => $"User Id=system;Password={Secret};Data Source={host}:{port}/FREEPDB1;Connection Timeout=2",
            "redis" => $"{host}:{port},password={Secret},connectTimeout=2000,connectRetry=0",
            "rabbitmq" => $"amqp://guest:{Secret}@{host}:{port}",
            "mongodb" => $"mongodb://root:{Secret}@{host}:{port}/{name}?authSource=admin&connectTimeoutMS=2000&serverSelectionTimeoutMS=2000",
            "kafka" => $"{host}:{port}",
            "nats" => $"nats://{host}:{port}",
            "elasticsearch" => $"http://elastic:{Secret}@{host}:{port}",
            "qdrant" => $"Endpoint=http://{host}:{port};Key={Secret}",
            "milvus" => $"Endpoint=http://{host}:{port};Key=root:{Secret}",
            // Not UseDevelopmentStorage=true: that is Azurite on this machine's own ports.
            "azure-storage" => $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={StorageEmulatorKey};"
                               + $"BlobEndpoint=http://{host}:{port}/devstoreaccount1;QueueEndpoint=http://{host}:{port}/devstoreaccount1;TableEndpoint=http://{host}:{port}/devstoreaccount1",
            "azure-servicebus" => $"Endpoint=sb://{host}:{port}/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey={Secret}",
            "azure-eventhubs" => $"Endpoint=sb://{host}:{port}/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey={Secret};EntityPath={name}",
            "cosmosdb" => $"AccountEndpoint=https://{host}:{port}/;AccountKey={CosmosEmulatorKey}",
            "ai" or "ollama" => $"Endpoint=http://{host}:{port};Key={Secret}",
            "seq" => $"http://{host}:{port}",
            "kurrentdb" => $"kurrentdb://admin:{Secret}@{host}:{port}?tls=false",
            "eventstore" => $"esdb://admin:{Secret}@{host}:{port}?tls=false",
            _ => $"Host={host};Port={port};Password={Secret}",
        };
    }

    /// <summary>What kind of thing an Aspire resource type is, for its fake connection string.</summary>
    public static string KindOfResourceType(string? type) => ResourceKinds.OfIdentifier(type) ?? "other";

    /// <summary>
    /// Replaces the manifest expressions in an Aspire environment value with fakes:
    /// {db.connectionString}, {api.bindings.http.url}, {param.value} and the rest. Another
    /// resource's endpoint is at <see cref="Host"/>:<see cref="Port"/>; the resource's own (its
    /// ASPNETCORE_URLS) at localhost, one port per endpoint, where nothing listens either: the
    /// probe's server binds nothing. A connection string is the resource's own expression, faked —
    /// <c>kurrentdb://{kurrentdb.bindings.http.host}:{kurrentdb.bindings.http.port}?tls=false</c> is
    /// <c>kurrentdb://127.0.0.1:1?tls=false</c>, the scheme its client insists on — and a guess by the
    /// resource's type only when the model has no expression, or one this cannot fake.
    /// </summary>
    public static string Resolve(string value, IReadOnlyDictionary<string, ProbeAspireResource> resources, string? self = null) =>
        Resolve(value, resources, self, depth: 0, out _);

    private static string Resolve(string value, IReadOnlyDictionary<string, ProbeAspireResource> resources, string? self, int depth, out bool unknown)
    {
        var anyUnknown = false;
        var resolved = Expression().Replace(value, match =>
        {
            var parts = match.Groups[1].Value.Split('.');
            var name = parts[0];
            resources.TryGetValue(name, out var resource);
            var property = parts.Length > 1 ? parts[1] : "";

            if (property == "connectionString")
            {
                // A conditional part of another resource's connection string, not a resource:
                // Aspire 13 writes redis as "…,password={redis-password.value}{cond-redis-bindings-
                // tcp-tlsenabled-….connectionString}", the part ",ssl=true" when TLS is on. Nothing
                // listens on a fake address, with TLS or without: the part is left out, rather than
                // guessed as a connection string of its own and glued onto the password.
                if (resource is null && name.StartsWith("cond-", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (resource?.ConnectionString is { } expression && depth < 6 && !expression.Contains(match.Value, StringComparison.Ordinal))
                {
                    var own = Resolve(expression, resources, self, depth + 1, out var cannot);
                    if (!cannot)
                    {
                        return own;
                    }
                }

                return ConnectionString(KindOfResourceType(resource?.Type), resource?.Name ?? name);
            }

            if (property == "bindings" && parts.Length >= 4)
            {
                var own = name == self;
                var host = own ? "localhost" : Host;
                var endpoints = resource?.Endpoints ?? [];
                var index = endpoints.FindIndex(e => e.Name == parts[2]);
                var endpoint = index < 0 ? null : endpoints[index];
                var https = parts[2].StartsWith("https", StringComparison.Ordinal);
                // The resource's own endpoints get a port each — http and https must not become two
                // URLs on one port; everyone else's are the one port that refuses.
                var port = !own ? Port : endpoint?.Port ?? endpoint?.TargetPort ?? 5000 + (index < 0 ? endpoints.Count + (https ? 1 : 0) : index);
                var scheme = endpoint?.Scheme ?? (https ? "https" : "http");
                var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                switch (parts[3])
                {
                    case "url": return $"{scheme}://{host}:{portText}";
                    case "host": return host;
                    case "port" or "targetPort": return portText;
                    case "hostAndPort": return $"{host}:{portText}";
                    case "scheme": return scheme;
                }
            }

            // Parameter values (and Aspire 8's inputs) are secrets as often as not.
            if (property is "value" or "inputs")
            {
                return Secret;
            }

            // Bicep outputs and the like: nothing to fake them from.
            anyUnknown = true;
            return Secret;
        });

        unknown = anyUnknown;
        return resolved;
    }

    /// <summary>A list of URLs, as ASPNETCORE_URLS takes them, with every port once: the first URL on a port stays.</summary>
    public static string DistinctPorts(string urls)
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var url in ListenUrls.Split(urls))
        {
            var port = ListenUrls.Parse(url)?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? url;
            if (ports.Add(port))
            {
                kept.Add(url);
            }
        }

        return string.Join(";", kept);
    }

    public static bool HasExpression(string value) => Expression().IsMatch(value);

    /// <summary>
    /// What a host asked for when it failed for want of a connection string, so the probe can be
    /// run again with one: Aspire's client integrations name the key and their configuration
    /// section (<c>'Aspire:Npgsql:EntityFrameworkCore:PostgreSQL'</c>), which says the kind; else the
    /// connection's own name may.
    /// </summary>
    public static (string Name, string Kind)? MissingConnectionString(IEnumerable<ProbeError> errors)
    {
        foreach (var error in errors)
        {
            var match = MissingConnectionStringMessage().Match(error.Message);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var section = IntegrationSection().Match(error.Message);
            var kind = (section.Success ? ResourceKinds.OfIdentifier(section.Groups[1].Value) : null)
                       ?? ResourceKinds.OfIdentifier(name)
                       ?? "other";
            return (name, kind);
        }

        return null;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+)*)\}")]
    private static partial Regex Expression();

    [GeneratedRegex(@"ConnectionStrings:([A-Za-z0-9_\-\.]+)")]
    private static partial Regex MissingConnectionStringMessage();

    [GeneratedRegex(@"'(Aspire:[^']+)'")]
    private static partial Regex IntegrationSection();
}

/// <summary>
/// What a resource is — postgres, redis, kafka, an observability or proxy container — by the words
/// that name it. The one table everything that guesses reads: an Aspire resource type, a client
/// integration's configuration section, a compose image or service name.
/// </summary>
internal static partial class ResourceKinds
{
    /// <summary>
    /// The kind; the words that name it anywhere; the words that name it only inside a framework's
    /// identifier — "storage" in AzureBlobStorageResource, but not in a storage-api image. The first
    /// row that matches wins, so the Azure messaging rows come before storage.
    /// </summary>
    private static readonly (string Kind, string[] Words, string[] IdentifierWords)[] Table =
    [
        ("postgres", ["postgres", "postgresql", "pgvector", "postgis", "npgsql", "timescaledb"], []),
        ("sqlserver", ["sqlserver", "sql-server", "mssql", "azure-sql-edge", "azuresql", "sqlclient"], []),
        ("mysql", ["mysql", "mariadb"], []),
        ("oracle", ["oracle"], []),
        ("redis", ["redis", "valkey", "garnet"], []),
        ("rabbitmq", ["rabbitmq"], []),
        ("mongodb", ["mongo", "mongodb"], []),
        ("kafka", ["kafka", "redpanda", "confluent"], []),
        ("nats", ["nats"], []),
        ("elasticsearch", ["elasticsearch", "opensearch"], []),
        ("qdrant", ["qdrant"], []),
        ("milvus", ["milvus"], []),
        ("azure-servicebus", ["servicebus", "service-bus"], []),
        ("azure-eventhubs", ["eventhubs", "eventhub", "event-hubs"], []),
        ("cosmosdb", ["cosmos", "cosmosdb"], []),
        ("azure-storage", ["azurite", "azure-storage"], ["storage", "tables"]),
        ("ollama", ["ollama"], []),
        ("ai", ["openai", "foundry", "inference"], ["model"]),
        ("seq", ["seq"], []),
        ("kurrentdb", ["kurrent", "kurrentdb"], []),
        ("eventstore", ["eventstore"], []),

        // Nothing to fake a connection string for: images, in practice.
        ("jaeger", ["jaeger", "jaegertracing"], []),
        ("zipkin", ["zipkin", "openzipkin"], []),
        ("opentelemetry", ["otel", "opentelemetry"], []),
        ("prometheus", ["prometheus"], []),
        ("grafana", ["grafana"], []),
        ("keycloak", ["keycloak"], []),
        ("s3", ["minio"], []),
        ("proxy", ["nginx", "envoy", "traefik"], []),
        ("consul", ["consul"], []),
        ("vault", ["vault"], []),
        ("memcached", ["memcached"], []),
        ("cassandra", ["cassandra"], []),
        ("clickhouse", ["clickhouse"], []),
    ];

    /// <summary>
    /// The kind a framework's identifier names — an Aspire resource type (PostgresServerResource),
    /// an integration's configuration section (Aspire:Confluent:Kafka) — by a word anywhere in it:
    /// identifiers run their words together.
    /// </summary>
    public static string? OfIdentifier(string? identifier)
    {
        var text = new string((identifier ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var (kind, words, identifierWords) in Table)
        {
            if (words.Concat(identifierWords).Any(w => text.Contains(w.Replace("-", ""), StringComparison.Ordinal)))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// The kind a name someone chose names — an image (mcr.microsoft.com/mssql/server), a compose
    /// service — by its words, the runs of letters and digits between '-', '_', '.', '/' and the
    /// like. One of them has to be a word of the table, or several in a row (azure-sql-edge):
    /// hotel-api is no otel collector, sequencer no seq, consultation-svc no consul.
    /// </summary>
    public static string? OfName(string? name)
    {
        var tokens = Words(name);
        if (tokens.Length == 0)
        {
            return null;
        }

        foreach (var (kind, words, _) in Table)
        {
            if (words.Any(w => ContainsRun(tokens, Words(w))))
            {
                return kind;
            }
        }

        return null;
    }

    private static string[] Words(string? text) => [.. NotAlphanumeric().Split((text ?? "").ToLowerInvariant()).Where(t => t.Length > 0)];

    private static bool ContainsRun(string[] tokens, string[] run)
    {
        for (var i = 0; i + run.Length <= tokens.Length; i++)
        {
            if (run.Select((word, j) => tokens[i + j] == word).All(equal => equal))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAlphanumeric();
}

/// <summary>The URLs a server listens on, as ASPNETCORE_URLS and a launch profile's applicationUrl list them.</summary>
internal static class ListenUrls
{
    public static string[] Split(string? urls) =>
        (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>One URL; <c>*</c> and <c>+</c> — any address — are no host Uri parses, so they are read as localhost.</summary>
    public static Uri? Parse(string url) =>
        Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) ? uri : null;
}
