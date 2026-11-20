using FusionModules;
using FusionModules.EntityFrameworkCore.TestModule;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

[assembly: HostingStartup(typeof(LegacyModule))]

namespace FusionModules.EntityFrameworkCore.TestModule;

internal sealed class LegacyModule : ModuleBase
{
}

/// <summary>This module's own copy of an order.</summary>
public sealed class LegacyOrder
{
    public int Id { get; set; }
}

/// <summary>The other module's table: what MOD0017 is about.</summary>
internal sealed class LegacyOrderConfiguration : IEntityTypeConfiguration<LegacyOrder>
{
    public void Configure(EntityTypeBuilder<LegacyOrder> builder) => builder.ToTable("Orders", "Sales");
}
