using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// The modules a context's model is composed from, carried in the context's options, where both the
/// model (<see cref="ModuleModelBuilderExtensions.ApplyModuleConfigurations"/>) and the model cache
/// key (<see cref="ModuleModelCacheKeyFactory"/>) find them.
/// </summary>
internal sealed class ModulesOptionsExtension(IReadOnlyList<Assembly> modules) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>The modules: the host's own first when it is one, then each after the modules it references.</summary>
    public IReadOnlyList<Assembly> Modules { get; } = modules;

    /// <summary>The module set as one string: part of the model cache key.</summary>
    public string Fingerprint { get; } = string.Join(';', modules.Select(module => module.GetName().Name));

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    // The module set changes the model, not the services: the cache key factory is put in place by
    // ReplaceService, which EF Core applies after every extension's services.
    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ModulesOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private new ModulesOptionsExtension Extension => (ModulesOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"Modules={Extension.Fingerprint} ";

        // One internal service provider whatever the modules: they change the model, which the
        // cache key tells apart, not the services.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["FusionModules:Modules"] = Extension.Fingerprint;
    }
}
