using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace FusionModules.Tool.Probe;

// Usage (started by the tool, not by hand):
//
//   dotnet exec --depsfile App.deps.json --runtimeconfig App.runtimeconfig.json
//               [--additionalprobingpath <nuget packages>] FusionModules.Tool.Probe.dll
//               --assembly <App.dll> --output <result.json> [--mode service|apphost]
//               [--timeout <seconds>] [--all-services] [-- <application arguments>]
//
// Always exits 0 once it has written its result; what went wrong is in the result's errors.
internal static class Program
{
    private static int Main(string[] args)
    {
        // The tool reads what the application writes as UTF-8; the console's own code page (866 on
        // a Russian Windows) would turn a localized exception message into replacement characters.
        Utf8Console();

        string? assemblyPath = null;
        string? output = null;
        var mode = "service";
        var timeout = TimeSpan.FromSeconds(60);
        var allServices = false;
        var applicationArguments = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly": assemblyPath = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--mode": mode = args[++i]; break;
                case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture)); break;
                case "--all-services": allServices = true; break;
                case "--fake-address": ErrorHints.FakeAddress = args[++i]; break;
                case "--":
                    applicationArguments.AddRange(args.Skip(i + 1));
                    i = args.Length;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    return 2;
            }
        }

        if (assemblyPath is null || output is null)
        {
            Console.Error.WriteLine("--assembly and --output are required.");
            return 2;
        }

        var result = new ProbeOutput { Mode = mode, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            assemblyPath = Path.GetFullPath(assemblyPath);
            var targetDirectory = Path.GetDirectoryName(assemblyPath)!;

            // The deps file lists the application's own assemblies as relative to the application's
            // directory, and the "application" the host started is this probe, in another directory.
            // Packages come from --additionalprobingpath; everything app-local comes from here.
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var candidate = Path.Combine(targetDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };

            var applicationAssemblies = DepsFile.ProjectLibraries(Path.ChangeExtension(assemblyPath, ".deps.json"));
            // FusionModules referenced as a project (as in its own samples) is still not the application.
            applicationAssemblies.RemoveWhere(ServiceProbe.IsFusionModules);
            result.ApplicationAssemblies = [.. applicationAssemblies.Order(StringComparer.OrdinalIgnoreCase)];
            ProbeError.ApplicationAssemblies = applicationAssemblies;

            Assembly target;
            try
            {
                target = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath)));
            }
            catch (FileNotFoundException)
            {
                target = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            }

            // What WebApplication.CreateBuilder takes the application name from, and with it user
            // secrets, MVC's application part and static web assets; what Aspire reads its AppHost
            // metadata from. Without this, all of them would describe the probe.
            if (!EntryAssembly.TrySet(target) && mode != "apphost")
            {
                // What HostFactoryResolver itself falls back to, and enough for WebApplicationBuilder.
                applicationArguments.AddRange(["--applicationName", target.GetName().Name!]);
            }

            if (mode == "apphost")
            {
                AppHostProbe.Run(target, [.. applicationArguments], timeout, result);
            }
            else
            {
                ServiceProbe.Run(target, [.. applicationArguments], timeout, applicationAssemblies, allServices, result);
            }
        }
        catch (Exception exception)
        {
            result.Errors.Add(ProbeError.From("probe", exception));
        }

        result.DurationMs = stopwatch.ElapsedMilliseconds;

        try
        {
            // Written beside and renamed into place: a probe killed while writing — its own timeout
            // reached the tool's — leaves no half a result for the tool to read.
            var temporary = output + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonFormat.Options));
            File.Move(temporary, output, overwrite: true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not write {output}: {exception}");
            Environment.Exit(3);
        }

        // Foreground threads the application started must not keep the probe alive.
        Environment.Exit(0);
        return 0;
    }

    /// <summary>Standard output and error as UTF-8 without a byte order mark, whatever the console's code page.</summary>
    private static void Utf8Console()
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch (IOException)
        {
            // No standard streams: nothing to read them as, either.
        }
    }
}

internal static class EntryAssembly
{
    /// <summary>
    /// Assembly.SetEntryAssembly is public from .NET 9; on .NET 8 the field behind it is set
    /// directly. Reflection either way, because the probe compiles against net8.0.
    /// </summary>
    public static bool TrySet(Assembly assembly)
    {
        try
        {
            if (typeof(Assembly).GetMethod("SetEntryAssembly", BindingFlags.Public | BindingFlags.Static, [typeof(Assembly)]) is { } method)
            {
                method.Invoke(null, [assembly]);
                return Assembly.GetEntryAssembly() == assembly;
            }

            if (typeof(Assembly).GetField("s_overriddenEntryAssembly", BindingFlags.NonPublic | BindingFlags.Static) is { } field)
            {
                field.SetValue(null, assembly);
                return Assembly.GetEntryAssembly() == assembly;
            }
        }
        catch
        {
            // Fall through to the argument.
        }

        return false;
    }
}

internal static class DepsFile
{
    public static HashSet<string> ProjectLibraries(string depsFile)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(depsFile))
        {
            return names;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
        if (document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "project")
                {
                    var slash = library.Name.IndexOf('/');
                    names.Add(slash < 0 ? library.Name : library.Name[..slash]);
                }
            }
        }

        return names;
    }
}
