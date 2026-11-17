using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

internal sealed class InventoryOptions
{
    public string Path { get; set; } = ".";

    public string? Output { get; set; }

    /// <summary>No restore, no build, no probe: project evaluation and deployment files only.</summary>
    public bool Static { get; set; }

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public string Configuration { get; set; } = "Debug";

    public string? Framework { get; set; }

    public string? WorkDirectory { get; set; }

    public string? EnvironmentName { get; set; }

    public string? LaunchProfile { get; set; }

    public List<string> ComposeFiles { get; set; } = [];

    public bool UseDockerCli { get; set; } = true;

    public bool AllServices { get; set; }

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>--env host:NAME=VALUE: for the host of that service, project or assembly name only.</summary>
    public List<(string Scope, string Name, string Value)> ScopedEnvironment { get; set; } = [];

    public int Parallelism { get; set; } = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4);

    public bool Quiet { get; set; }

    /// <summary>--compare: an inventory taken before, to compare this one with.</summary>
    public string? Compare { get; set; }
}

/// <summary><c>dotnet modules compare</c>: two inventories, or with --merged, one host against the services it took in.</summary>
internal sealed record CompareOptions(string Before, string After, string? Host, List<string> Services);

/// <summary><c>dotnet modules topology</c>: the hosts of one inventory as one host.</summary>
internal sealed record TopologyOptions(string Inventory, List<string> Hosts, bool Pairs);

internal static class Program
{
    private const string Usage = """
        Usage: dotnet modules inventory [path] [options]
               dotnet modules compare <before.json> <after.json> [--merged <host> <service>...]
               dotnet modules topology <inventory.json> [<service>...] [--pairs]

        inventory: what a solution deploys and what each host composes, as JSON.

          path                     A solution (.sln, .slnx, .slnf), a project (.csproj), or a directory
                                   holding exactly one of either — or, holding neither, exactly one
                                   solution in the directories below it (three levels). Defaults to
                                   the current directory.

        Options:
          -o, --output <file>      Write the JSON here instead of to standard output.
          --static                 Evaluate projects and read deployment files only: no restore, no
                                   build, no probe.
          --timeout <seconds>      How long one host may take to start under the probe (default 60).
          -c, --configuration <c>  Build configuration (default Debug).
          -f, --framework <tfm>    Target framework to build and probe multi-targeted hosts with
                                   (default: the highest).
          --environment <name>     ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT for every probed host
                                   (default: the deployment's own, else Development).
          --env <NAME=VALUE>       An extra environment variable for every probed host. Repeatable.
          --env <host:NAME=VALUE>  The same for one host, named by its service, project or assembly
                                   name.
          --launch-profile <name>  The launchSettings.json profile whose environment a host without
                                   orchestration is probed with (default: the first "Project" profile).
          --compose-file <file>    Use these compose files rather than discovering them, even beside an
                                   Aspire AppHost. Repeatable.
          --no-docker              Parse compose files directly, without `docker compose config`.
          --all-services           List every DI registration, not only those touching the solution's
                                   own assemblies.
          --work-dir <dir>         Where restore and build output go (default: a directory under the
                                   system temp directory, kept between runs for incremental builds).
          --parallel <n>           Probes to run at once (default: half the processors, at most 4).
          -q, --quiet              No progress on standard error.
          --compare <file>         Compare with an inventory taken before, as `compare` does, and exit
                                   with 2 when they differ.

        compare: whether a step changed what a host composes — its endpoints, registrations, hosted
        services, DbContexts with their models, and errors — host by host. Exit code 0 the same,
        2 different.
          --merged <host> <service>...  Compare the host of the second inventory with the services of
                                   the first that it took in.

        topology: what one host composing these hosts' services would report — what fails startup,
        what fails requests, what to reconcile — before anyone builds it. All hosts when none is
        named. Exit code 2 when it would fail startup.
          --pairs                  One line per pair of hosts instead: which ones merge cleanly.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "-?")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "--version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return args[0] switch
            {
                "inventory" => await InventoryAsync(Parse(args[1..])),
                "compare" => await CompareAsync(ParseCompare(args[1..])),
                "topology" => await TopologyAsync(ParseTopology(args[1..])),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run 'dotnet modules --help' for the commands and their options.");
            return 1;
        }
        catch (HelpRequested)
        {
            Console.WriteLine(Usage);
            return 0;
        }
        catch (InventoryInputException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    private static async Task<int> InventoryAsync(InventoryOptions options)
    {
        // Read first: a wrong path should not cost a build of the solution.
        var baseline = options.Compare is null ? null : await ReadAsync(options.Compare);

        Action<string> log = options.Quiet ? _ => { } : message => Console.Error.WriteLine(message);
        var document = await new Inventory(options, log).RunAsync();
        var json = JsonSerializer.Serialize(document, JsonFormat.Options);
        if (options.Output is null)
        {
            Console.Out.WriteLine(json);
        }
        else
        {
            await File.WriteAllTextAsync(options.Output, json);
            log($"Wrote {options.Output}: {document.Deployment.Services.Count} deployment services, {document.Projects.Count} projects, {document.Hosts.Count} hosts, {document.Errors.Count} errors.");
        }

        if (baseline is null)
        {
            return 0;
        }

        // Beside the JSON, not into it, when the JSON goes to standard output.
        var comparison = Comparison.Compare(baseline, document);
        comparison.Write(options.Output is null ? Console.Error : Console.Out, options.Compare!, options.Output ?? "this inventory");
        return comparison.Same ? 0 : 2;
    }

    private static async Task<int> CompareAsync(CompareOptions options)
    {
        var before = await ReadAsync(options.Before);
        var after = await ReadAsync(options.After);
        var comparison = options.Host is null
            ? Comparison.Compare(before, after)
            : Comparison.CompareMerged(before, options.Services, after, options.Host);
        comparison.Write(Console.Out, options.Before, options.After);
        return comparison.Same ? 0 : 2;
    }

    private static async Task<int> TopologyAsync(TopologyOptions options)
    {
        var inventory = await ReadAsync(options.Inventory);
        if (options.Pairs)
        {
            foreach (var line in Topology.Pairs(inventory, options.Hosts))
            {
                Console.WriteLine(line);
            }

            return 0;
        }

        var report = Topology.Preview(inventory, options.Hosts);
        report.Write(Console.Out);
        return report.FailsStartup ? 2 : 0;
    }

    /// <summary>An inventory document, or an input error that says what is wrong with the file.</summary>
    internal static async Task<InventoryDocument> ReadAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new InventoryInputException($"No inventory at {path}.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<InventoryDocument>(stream, JsonFormat.Options) is { Tool.Length: > 0 } document
                ? document
                : throw new InventoryInputException($"{path} is not an inventory: take one with `dotnet modules inventory -o {System.IO.Path.GetFileName(path)}`.");
        }
        catch (JsonException exception)
        {
            throw new InventoryInputException($"{path} is not an inventory: {exception.Message}");
        }
    }

    internal static CompareOptions ParseCompare(string[] args)
    {
        var (files, flags) = Split(args, "--merged");
        var merged = flags.GetValueOrDefault("--merged");
        if (files.Count != 2 || merged is { Count: < 2 })
        {
            throw new ArgumentException("compare expects two inventories, and after --merged a host and the services it took in.");
        }

        return new CompareOptions(files[0], files[1], merged?[0], merged is null ? [] : merged[1..]);
    }

    internal static TopologyOptions ParseTopology(string[] args)
    {
        var (names, flags) = Split(args, "--pairs");
        if (names.Count == 0 || flags.GetValueOrDefault("--pairs") is { Count: > 0 })
        {
            throw new ArgumentException("topology expects an inventory, then the services to compose, then --pairs if wanted.");
        }

        return new TopologyOptions(names[0], names[1..], flags.ContainsKey("--pairs"));
    }

    /// <summary>The arguments before the first flag, and each flag's own: the arguments up to the next flag.</summary>
    private static (List<string> Positionals, Dictionary<string, List<string>> Flags) Split(string[] args, params string[] known)
    {
        var positionals = new List<string>();
        var flags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var current = positionals;
        foreach (var argument in args)
        {
            if (argument is "-h" or "--help")
            {
                throw new HelpRequested();
            }

            if (argument.StartsWith('-'))
            {
                current = known.Contains(argument) && !flags.ContainsKey(argument)
                    ? flags[argument] = []
                    : throw new ArgumentException($"Unknown argument '{argument}'.");
            }
            else
            {
                current.Add(argument);
            }
        }

        return (positionals, flags);
    }

    internal static InventoryOptions Parse(string[] args)
    {
        var options = new InventoryOptions();
        var pathSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{argument} needs a value.");
                }

                return args[++i];
            }

            switch (argument)
            {
                case "-h" or "--help": throw new HelpRequested();
                case "-o" or "--output": options.Output = Value(); break;
                case "--static": options.Static = true; break;
                case "--timeout":
                    var seconds = Value();
                    options.ProbeTimeout = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout) && timeout > 0 && timeout <= int.MaxValue / 1000
                        ? TimeSpan.FromSeconds(timeout)
                        : throw new ArgumentException($"--timeout expects a positive number of seconds, not '{seconds}'.");
                    break;
                case "-c" or "--configuration": options.Configuration = Value(); break;
                case "-f" or "--framework": options.Framework = Value(); break;
                case "--environment": options.EnvironmentName = Value(); break;
                case "--launch-profile": options.LaunchProfile = Value(); break;
                case "--compose-file": options.ComposeFiles.Add(Value()); break;
                case "--no-docker": options.UseDockerCli = false; break;
                case "--all-services": options.AllServices = true; break;
                case "--work-dir": options.WorkDirectory = Value(); break;
                case "--parallel":
                    var count = Value();
                    options.Parallelism = int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parallelism) && parallelism >= 1
                        ? parallelism
                        : throw new ArgumentException($"--parallel expects a whole number of at least 1, not '{count}'.");
                    break;
                case "-q" or "--quiet": options.Quiet = true; break;
                case "--compare": options.Compare = Value(); break;
                case "--env":
                    var pair = Value();
                    var equals = pair.IndexOf('=');
                    if (equals <= 0)
                    {
                        throw new ArgumentException($"--env expects NAME=VALUE, not '{pair}'.");
                    }

                    var key = pair[..equals];
                    var colon = key.IndexOf(':');
                    if (colon > 0 && colon < key.Length - 1)
                    {
                        // host:NAME=VALUE, for one host; decided against the hosts once they are known.
                        options.ScopedEnvironment.Add((key[..colon], key[(colon + 1)..], pair[(equals + 1)..]));
                    }
                    else
                    {
                        options.Environment[key] = pair[(equals + 1)..];
                    }

                    break;
                default:
                    if (argument.StartsWith('-') || pathSet)
                    {
                        throw new ArgumentException($"Unknown argument '{argument}'.");
                    }

                    options.Path = argument;
                    pathSet = true;
                    break;
            }
        }

        return options;
    }

    private sealed class HelpRequested : Exception;
}
