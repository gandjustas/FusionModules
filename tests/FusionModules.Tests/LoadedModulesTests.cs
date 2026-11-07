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

    [Test]
    public async Task AModuleComesAfterTheModulesItReferences()
    {
        // By name FusionModules would come first; it references the hosting abstractions, so they
        // come first, whatever order the registry or HOSTINGSTARTUPASSEMBLIES gives.
        var loaded = ModuleBase.GetLoadedModules(Build(
            (typeof(ModuleBase).Assembly, typeof(ModuleBase)),
            (typeof(IHostingStartup).Assembly, typeof(IHostingStartup))));

        await Assert.That(loaded.Select(assembly => assembly.GetName().Name).ToArray())
            .IsEquivalentTo(new[] { typeof(IHostingStartup).Assembly.GetName().Name, typeof(ModuleBase).Assembly.GetName().Name }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ModulesThatDoNotDependOnEachOtherComeByName()
    {
        // Neither references the other. By name, not by the variable: one set of modules has one
        // order in every topology, so one EF Core model.
        var loaded = ModuleBase.GetLoadedModules(Build(
            (typeof(ConfigurationBuilder).Assembly, typeof(ConfigurationBuilder)),
            (typeof(IHostingStartup).Assembly, typeof(IHostingStartup))));

        await Assert.That(loaded.Select(assembly => assembly.GetName().Name).ToArray())
            .IsEquivalentTo(new[] { typeof(IHostingStartup).Assembly.GetName().Name, typeof(ConfigurationBuilder).Assembly.GetName().Name }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheHostsOwnAssemblyComesFirst()
    {
        // The host's module is activated without being named in HOSTINGSTARTUPASSEMBLIES, and
        // before the modules that are; by name it would come last here.
        var configuration = Build(
            (typeof(LoadedModulesTests).Assembly, typeof(Probe)),
            (typeof(IHostingStartup).Assembly, typeof(IHostingStartup)));
        var host = typeof(ConfigurationBuilder).Assembly;
        configuration[$"{Registry}:{host.GetName().Name}"] = typeof(ConfigurationBuilder).AssemblyQualifiedName;

        var loaded = ModuleBase.GetLoadedModules(configuration);

        await Assert.That(loaded[0]).IsEqualTo(host);
        await Assert.That(loaded.Count).IsEqualTo(3);
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
