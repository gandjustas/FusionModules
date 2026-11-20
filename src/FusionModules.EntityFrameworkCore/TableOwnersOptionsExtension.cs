using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.EntityFrameworkCore;

/// <summary>
/// What <see cref="ModuleDbContextOptionsExtensions.CheckModuleTableOwners(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>
/// adds to a context's options: the model-finalizing convention that compares the table owners
/// <see cref="ModuleTables"/> recorded, and, by being there, the request to record them.
/// </summary>
internal sealed class TableOwnersOptionsExtension : IDbContextOptionsExtension
{
    public static readonly TableOwnersOptionsExtension Instance = new();

    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, Plugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    /// <summary>Adds the convention last, after the provider's and the relational ones, so it sees the tables they settle.</summary>
    private sealed class Plugin : IConventionSetPlugin
    {
        public ConventionSet ModifyConventions(ConventionSet conventionSet)
        {
            conventionSet.ModelFinalizingConventions.Add(new Convention());
            return conventionSet;
        }
    }

    private sealed class Convention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) =>
            ModuleTables.Check(modelBuilder.Metadata);
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "CheckModuleTableOwners ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["FusionModules:CheckModuleTableOwners"] = "1";
    }
}
