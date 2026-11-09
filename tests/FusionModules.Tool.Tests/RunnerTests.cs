using FusionModules.Tool.Probe;

namespace FusionModules.Tool.Tests;

/// <summary>The processes the tool starts, the arguments it takes, and where each probe runs.</summary>
public class RunnerTests
{
    private static (string File, string[] Arguments) Shell(string windows, string unix) =>
        OperatingSystem.IsWindows() ? ("cmd.exe", ["/d", "/c", windows]) : ("/bin/sh", ["-c", unix]);

    [Test]
    public async Task AProcessPastItsTimeout_IsKilled()
    {
        var (file, arguments) = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");

        var result = await ProcessRunner.RunAsync(file, arguments, Path.GetTempPath(), TimeSpan.FromSeconds(1));

        await Assert.That(result.TimedOut).IsTrue();
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Elapsed).IsLessThan(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task AGrandchildHoldingTheOutput_DoesNotHoldTheRun()
    {
        // The child exits at once; the grandchild it leaves behind keeps the output open, as
        // SpaProxy's npm does under a probed host.
        var (file, arguments) = Shell("start /b ping -n 30 127.0.0.1 & echo done", "sleep 30 & echo done");

        var result = await ProcessRunner.RunAsync(file, arguments, Path.GetTempPath(), TimeSpan.FromSeconds(25));

        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.StandardOutput).Contains("done");
        await Assert.That(result.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task AnApplicationProcess_DoesNotInheritTheShellsApplicationConfiguration()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var connectionString = $"ConnectionStrings__FusionModulesToolTest{id}";
        var plain = $"FUSION_MODULES_TOOL_TEST_{id}";
        Environment.SetEnvironmentVariable(connectionString, "Host=real-database");
        Environment.SetEnvironmentVariable(plain, "kept");
        try
        {
            var (file, arguments) = Shell("set", "env");
            var given = new Dictionary<string, string> { [$"ConnectionStrings__Given{id}"] = "Host=127.0.0.1" };

            var application = await ProcessRunner.RunAsync(file, arguments, Path.GetTempPath(), TimeSpan.FromSeconds(30), given, application: true);
            var build = await ProcessRunner.RunAsync(file, arguments, Path.GetTempPath(), TimeSpan.FromSeconds(30));

            await Assert.That(application.StandardOutput).DoesNotContain(connectionString);
            await Assert.That(application.StandardOutput).Contains($"{plain}=kept");
            await Assert.That(application.StandardOutput).Contains($"ConnectionStrings__Given{id}=Host=127.0.0.1");
            await Assert.That(build.StandardOutput).Contains(connectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionString, null);
            Environment.SetEnvironmentVariable(plain, null);
        }
    }

    [Test]
    [Arguments("ConnectionStrings__db", true)]
    [Arguments("Logging:LogLevel:Default", true)]
    [Arguments("ASPNETCORE_URLS", true)]
    [Arguments("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", true)]
    [Arguments("HOSTINGSTARTUPASSEMBLIES", true)]
    [Arguments("DOTNET_STARTUP_HOOKS", true)]
    [Arguments("DOTNET_ENVIRONMENT", true)]
    [Arguments("AZURE_CLIENT_SECRET", true)]
    [Arguments("AWS_ACCESS_KEY_ID", true)]
    [Arguments("GITHUB_TOKEN", true)]
    [Arguments("PATH", false)]
    [Arguments("TEMP", false)]
    [Arguments("NUGET_PACKAGES", false)]
    [Arguments("DOTNET_ROOT", false)]
    [Arguments("DOTNET_ROOT(x86)", false)]
    [Arguments("DOTNET_HOST_PATH", false)]
    [Arguments("DOTNET_CLI_HOME", false)]
    [Arguments("DOTNET_gcServer", false)]
    public async Task WhatAnApplicationProcessDoesNotInherit(string name, bool dropped) =>
        await Assert.That(ProcessRunner.IsApplicationSetting(name)).IsEqualTo(dropped);

    [Test]
    [Arguments("--timeout", "abc")]
    [Arguments("--timeout", "0")]
    [Arguments("--timeout", "-5")]
    [Arguments("--parallel", "x")]
    [Arguments("--parallel", "0")]
    [Arguments("--env", "NOEQUALS")]
    public async Task ABadArgument_IsAUsageError(string option, string value) =>
        await Assert.That(() => Program.Parse([".", option, value])).Throws<ArgumentException>();

    [Test]
    public async Task AnOptionWithoutItsValue_IsAUsageError() =>
        await Assert.That(() => Program.Parse([".", "--timeout"])).Throws<ArgumentException>().WithMessageContaining("needs a value");

    [Test]
    public async Task CompareAndTopology_TakeTheirArguments()
    {
        var plain = Program.ParseCompare(["a.json", "b.json"]);
        await Assert.That((plain.Before, plain.After, plain.Host, plain.Services.Count)).IsEqualTo(("a.json", "b.json", (string?)null, 0));
        var merged = Program.ParseCompare(["a.json", "b.json", "--merged", "webapp", "catalog-api", "basket-api"]);
        await Assert.That((merged.Host, merged.Services.Count)).IsEqualTo(("webapp", 2));

        var topology = Program.ParseTopology(["inventory.json", "catalog-api", "ordering-api", "--pairs"]);
        await Assert.That((topology.Inventory, topology.Hosts.Count, topology.Pairs)).IsEqualTo(("inventory.json", 2, true));
        await Assert.That(Program.Parse([".", "--compare", "before.json"]).Compare).IsEqualTo("before.json");
    }

    [Test]
    [Arguments("compare", new[] { "a.json" })]
    [Arguments("compare", new[] { "a.json", "b.json", "--merged", "webapp" })]
    [Arguments("compare", new[] { "a.json", "b.json", "--pairs" })]
    [Arguments("topology", new string[0])]
    [Arguments("topology", new[] { "inventory.json", "--pairs", "catalog-api" })]
    public async Task CompareAndTopology_RefuseWhatTheyDoNotTake(string command, string[] args) =>
        await Assert.That(() => command == "compare" ? (object)Program.ParseCompare(args) : Program.ParseTopology(args)).Throws<ArgumentException>();

    [Test]
    public async Task AFileThatIsNotAnInventory_IsAnInputError()
    {
        using var fixture = new Fixture(("other.json", """{ "name": "not an inventory" }"""), ("broken.json", "{"));

        await Assert.That(async () => await Program.ReadAsync(fixture["other.json"])).Throws<InventoryInputException>().WithMessageContaining("is not an inventory");
        await Assert.That(async () => await Program.ReadAsync(fixture["broken.json"])).Throws<InventoryInputException>().WithMessageContaining("is not an inventory");
        await Assert.That(async () => await Program.ReadAsync(fixture["missing.json"])).Throws<InventoryInputException>().WithMessageContaining("No inventory");
    }

    [Test]
    public async Task TwoServicesOfOneProject_RunInWorkingDirectoriesOfTheirOwn()
    {
        using var fixture = new Fixture();
        var workspace = new Workspace(fixture.Root, "Debug");

        var api = HostProber.WorkingDirectory(workspace, "catalog-api");
        var worker = HostProber.WorkingDirectory(workspace, "catalog-worker");

        await Assert.That(api).IsNotEqualTo(worker);
        await Assert.That(Path.GetFileName(HostProber.WorkingDirectory(workspace, "a/b:c"))).IsEqualTo("a_b_c");
        await Assert.That(Path.GetDirectoryName(HostProber.WorkingDirectory(workspace, "../x"))).IsEqualTo(Path.Combine(fixture.Root, "cwd"));
    }

    [Test]
    public async Task AHalfWrittenResult_IsAnErrorOfThatHost_NotTheRunsEnd()
    {
        using var fixture = new Fixture(("result.json", """{ "mode": "service", "status": "sta"""));

        var probe = HostProber.ReadResult(fixture["result.json"], "service")!;

        await Assert.That(probe.Status).IsEqualTo("failed");
        await Assert.That(probe.Errors.Single().Message).StartsWith("The probe's result could not be read");
        await Assert.That(File.Exists(fixture["result.json"])).IsFalse();
    }
}
