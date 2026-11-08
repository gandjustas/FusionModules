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
}

internal static class Program
{
    private const string Usage = """
        Usage: dotnet modules inventory [path] [options]

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

        if (args[0] != "inventory")
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        InventoryOptions options;
        try
        {
            options = Parse(args[1..]);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run 'dotnet modules inventory --help' for the options.");
            return 1;
        }
        catch (HelpRequested)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Action<string> log = options.Quiet ? _ => { } : message => Console.Error.WriteLine(message);
        InventoryDocument document;
        try
        {
            document = await new Inventory(options, log).RunAsync();
        }
        catch (InventoryInputException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

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

        return 0;
    }

    internal static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

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
