using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace FusionModules.Tests;

/// <summary>
/// <see cref="ModuleBase.GetLoadedModules"/> is asked on every DbContext a request creates, so its
/// answer is kept per registry. These pin down what "per registry" has to mean.
/// </summary>
public class LoadedModulesTests
{
    private const string Registry = "FusionModules:Modules";

    [Test]
    public async Task TheSameRegistryGetsTheSameAnswer()
    {
        var configuration = Build((typeof(LoadedModulesTests).Assembly, typeof(Probe)));

        var first = ModuleBase.GetLoadedModules(configuration);
        var second = ModuleBase.GetLoadedModules(configuration);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first).Contains(typeof(LoadedModulesTests).Assembly);
    }

    [Test]
    public async Task ARegistryThatChangesIsAnsweredAfresh()
    {
        // Modules write themselves to the registry while the host's configuration is assembled.
        // Anything that asked half-way through must not pin the half-written answer.
        var configuration = Build();
        var before = ModuleBase.GetLoadedModules(configuration);

        configuration[$"{Registry}:{typeof(LoadedModulesTests).Assembly.GetName().Name}"] = typeof(Probe).AssemblyQualifiedName;
        var after = ModuleBase.GetLoadedModules(configuration);

        await Assert.That(before).IsEmpty();
        await Assert.That(after).Contains(typeof(LoadedModulesTests).Assembly);
    }

    [Test]
    public async Task TheSharedAnswerCannotBeRearranged()
    {
        // A caller holding an array could reorder it for everyone who asks after.
        var loaded = ModuleBase.GetLoadedModules(Build((typeof(LoadedModulesTests).Assembly, typeof(Probe))));

        await Assert.That(loaded is Assembly[]).IsFalse();
    }

    private static IConfigurationRoot Build(params (Assembly Assembly, Type Module)[] modules) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                modules
                    .Select(module => new KeyValuePair<string, string?>(
                        $"{Registry}:{module.Assembly.GetName().Name}", module.Module.AssemblyQualifiedName))
                    .Append(new(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules.Select(m => m.Assembly.GetName().Name)))))
            .Build();

    private sealed class Probe : ModuleBase;
}
