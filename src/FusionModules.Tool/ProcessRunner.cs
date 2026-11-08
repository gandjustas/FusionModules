using System.Diagnostics;
using System.Text;
using FusionModules.Tool.Probe;

namespace FusionModules.Tool;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public string Output => StandardOutput + StandardError;
}

internal static class ProcessRunner
{
    /// <summary>
    /// What `dotnet run` / `dotnet test` put in the environment of the process they start, and what
    /// then sends a child `dotnet build` to the wrong MSBuild. Never passed on.
    /// </summary>
    private static readonly string[] Scrubbed =
    [
        "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly",
        "DOTNET_HOST_FACTORY_RESOLVER_DEFAULT_TIMEOUT_IN_SECONDS", "DOTNET_LAUNCH_PROFILE",
    ];

    /// <summary>The DOTNET_ variables an application process inherits: where the runtime is, and how it runs — never what the application is configured with.</summary>
    private static readonly string[] HostVariablePrefixes =
    [
        "DOTNET_ROOT", "DOTNET_HOST_PATH", "DOTNET_CLI_", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
        "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_ROLL_FORWARD", "DOTNET_GC",
    ];

    /// <summary>Cloud SDKs read their credentials from these; a probed host must not get the developer's.</summary>
    private static readonly string[] CredentialPrefixes = ["AZURE_", "AWS_", "GOOGLE_", "GCLOUD_"];

    /// <summary>How long the output may take to close once the process has exited.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a process to its end or its timeout, whichever comes first, killing its tree on timeout.
    /// <paramref name="application"/> marks a process that runs the application's own code (the
    /// probe, the AppHost's manifest publisher): it does not inherit the shell's application
    /// configuration (<see cref="IsApplicationSetting"/>), only what <paramref name="environment"/> gives it.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default,
        bool application = false)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in Scrubbed)
        {
            start.Environment.Remove(name);
        }

        if (application)
        {
            foreach (var name in start.Environment.Keys.Where(IsApplicationSetting).ToList())
            {
                start.Environment.Remove(name);
            }
        }

        // English diagnostics, so that what the tool reports and matches on reads the same everywhere;
        // no first-run banner or workload update notice in an output that is parsed as JSON.
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => Append(stdout, stdoutClosed, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, stderrClosed, e.Data);
        process.Exited += (_, _) => exited.TrySetResult();

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The Exited event, not WaitForExitAsync: that also waits for the output to close, which a
        // grandchild holding it open (SpaProxy's npm) delays past the process's own end.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await exited.Task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited in the meantime.
            }

            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        // What arrived by the time the output closes, or a few seconds after the process ended.
        await Task.WhenAny(Task.WhenAll(stdoutClosed.Task, stderrClosed.Task), Task.Delay(DrainTimeout, CancellationToken.None));
        string output, error;
        lock (stdout)
        {
            output = stdout.ToString();
        }

        lock (stderr)
        {
            error = stderr.ToString();
        }

        return new ProcessResult(timedOut || !process.HasExited ? -1 : process.ExitCode, output, error, timedOut, stopwatch.Elapsed);
    }

    private static void Append(StringBuilder buffer, TaskCompletionSource closed, string? line)
    {
        if (line is null)
        {
            closed.TrySetResult();
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(line);
        }
    }

    /// <summary>
    /// Whether an inherited environment variable configures the application rather than the
    /// runtime, so that a process running the application's code must not inherit it from the shell
    /// the tool runs in: a configuration key (any name with <c>__</c> or <c>:</c> —
    /// <c>ConnectionStrings__db</c>, a real connection string that would beat the fake one), every
    /// <c>ASPNETCORE_*</c> (<c>ASPNETCORE_URLS</c>, hosting startup assemblies), <c>DOTNET_*</c> but
    /// the runtime's own (<see cref="HostVariablePrefixes"/>; <c>DOTNET_STARTUP_HOOKS</c> loads code),
    /// <c>HOSTINGSTARTUPASSEMBLIES</c>, cloud SDK credentials (<c>AZURE_*</c>, <c>AWS_*</c>,
    /// <c>GOOGLE_*</c>) and anything named like a secret (<c>GITHUB_TOKEN</c>).
    /// </summary>
    internal static bool IsApplicationSetting(string name)
    {
        if (name.Contains("__", StringComparison.Ordinal) || name.Contains(':'))
        {
            return true;
        }

        if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
        {
            return !HostVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        return name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
               || name.Equals("HOSTINGSTARTUPASSEMBLIES", StringComparison.OrdinalIgnoreCase)
               || name.Equals("HOSTINGSTARTUPEXCLUDEASSEMBLIES", StringComparison.OrdinalIgnoreCase)
               || CredentialPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
               || Secrets.IsSecretName(name);
    }

    /// <summary>The last lines of a process's output, for an error message.</summary>
    public static string Tail(string text, int lines = 15)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }
}
