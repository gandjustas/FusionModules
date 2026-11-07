using FusionModules;
using FusionModules.EntityFrameworkCore;
using FusionModules.EntityFrameworkCore.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// This test assembly is a module of its own: one module class, one entity, one configuration.
[assembly: HostingStartup(typeof(OrdersModule))]

namespace FusionModules.EntityFrameworkCore.Tests;

/// <summary>
/// <c>UseModules</c> and <c>ApplyModuleConfigurations</c>: a model composed from the modules a context's
/// options name, one cached model per set of modules.
/// </summary>
public class ModuleModelTests
{
    private const string Module = "FusionModules.EntityFrameworkCore.Tests";

    [Test]
    public async Task AContextOverAModule_HasItsEntities()
    {
        using var context = Create(options => options.UseModules(Module));

        var order = context.Model.FindEntityType(typeof(Order));
        await Assert.That(order).IsNotNull();
        await Assert.That(order!.GetTableName()).IsEqualTo("Orders");
        await Assert.That(order.GetSchema()).IsEqualTo("Sales");
    }

    [Test]
    public async Task TwoModuleSetsInOneProcess_GetTwoModels()
    {
        // The default cache key is the context type: the second context would be handed the first
        // one's model, whichever came first.
        using var without = Create(options => options.UseModules());
        using var with = Create(options => options.UseModules(Module));
        using var withoutAgain = Create(options => options.UseModules());

        await Assert.That(without.Model.FindEntityType(typeof(Order))).IsNull();
        await Assert.That(with.Model.FindEntityType(typeof(Order))).IsNotNull();
        await Assert.That(withoutAgain.Model.FindEntityType(typeof(Order))).IsNull();
    }

    [Test]
    public async Task TheHostsRegistry_NamesTheModules()
    {
        // What a host's configuration holds once its modules have activated.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(Module))
            .Build();

        using var context = Create(options => options.UseModules(configuration));

        await Assert.That(context.Model.FindEntityType(typeof(Order))).IsNotNull();
    }

    [Test]
    public async Task TheModelsComposition_IsLoggedOnce()
    {
        var entries = new List<(string Message, Dictionary<string, object?> Fields)>();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(new EntryProvider(entries)));

        // A context type of its own, so that no other test has built and cached this model already.
        var options = new DbContextOptionsBuilder<LoggedContext>()
            .UseSqlite("DataSource=:memory:")
            .UseLoggerFactory(factory)
            .UseModules(Module)
            .Options;
        using (var first = new LoggedContext(options))
        {
            _ = first.Model;
        }

        using (var second = new LoggedContext(options))
        {
            _ = second.Model;
        }

        var composition = entries.Select(entry => entry.Message).Where(line => line.StartsWith("Model of LoggedContext:", StringComparison.Ordinal)).ToList();
        await Assert.That(composition).HasSingleItem();
        await Assert.That(composition[0]).Contains($"configurations from: {Module} (Order)");
    }

    [Test]
    public async Task TheModelsLine_IsLabelledLikeTheTopologyLine_AndEveryPartIsAField()
    {
        var entries = Compose<LabelledContext>(Module);

        // One module, with a configuration: nothing without one, no entity configured twice — and a
        // configuration EF Core skips, without a parameterless constructor, is not named. The parts
        // left out of the line are still fields, so a log store finds the same ones for every model.
        var entry = entries.Single(entry => entry.Message.StartsWith("Model of LabelledContext:", StringComparison.Ordinal));
        await Assert.That(entry.Message).IsEqualTo($"Model of LabelledContext: configurations from: {Module} (Order)");
        await Assert.That((string)entry.Fields["{OriginalFormat}"]!).IsEqualTo("Model of {Context}: configurations from: {Modules}");
        await Assert.That((string)entry.Fields["Context"]!).IsEqualTo(nameof(LabelledContext));
        await Assert.That((string)entry.Fields["Without"]!).IsEmpty();
        await Assert.That((string)entry.Fields["Shared"]!).IsEmpty();
    }

    [Test]
    public async Task TheModuleSetKey_IsWhatAContextsOwnCacheKeyIncludes()
    {
        using var composed = Create(options => options.UseModules(Module));
        using var plain = new DbContext(new DbContextOptionsBuilder().UseSqlite("DataSource=:memory:").Options);

        await Assert.That(composed.GetModuleSetKey()).IsEqualTo(Module);
        await Assert.That(plain.GetModuleSetKey()).IsNull();
    }

    [Test]
    public async Task AModelOfNoModules_SaysSo()
    {
        var entries = Compose<EmptyContext>();

        await Assert.That(entries.Select(entry => entry.Message)).Contains("Model of EmptyContext: no modules");
    }

    /// <summary>Builds the model of a context type no other test uses, and returns every entry logged.</summary>
    private static List<(string Message, Dictionary<string, object?> Fields)> Compose<TContext>(params string[] modules)
        where TContext : DbContext
    {
        var entries = new List<(string Message, Dictionary<string, object?> Fields)>();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(new EntryProvider(entries)));

        var options = new DbContextOptionsBuilder<TContext>()
            .UseSqlite("DataSource=:memory:")
            .UseLoggerFactory(factory)
            .UseModules(modules)
            .Options;
        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        _ = context.Model;

        return entries;
    }

    [Test]
    public async Task WithoutUseModules_TheModelSaysWhatIsMissing()
    {
        using var context = Create(_ => { });

        var exception = await Assert.That(() => context.Model).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("UseModules");
    }

    private static ShopContext Create(Action<DbContextOptionsBuilder<ShopContext>> configure)
    {
        var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite("DataSource=:memory:");
        configure(options);
        return new ShopContext(options.Options);
    }
}

internal sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class LoggedContext(DbContextOptions<LoggedContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class LabelledContext(DbContextOptions<LabelledContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

/// <summary>Collects every entry logged, with its named values, whatever the category.</summary>
internal sealed class EntryProvider(List<(string Message, Dictionary<string, object?> Fields)> entries) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new EntryLogger(entries);

    public void Dispose()
    {
    }

    private sealed class EntryLogger(List<(string Message, Dictionary<string, object?> Fields)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add((
                    formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value) : []));
            }
        }
    }
}

internal sealed class OrdersModule : ModuleBase
{
}

public sealed class Order
{
    public int Id { get; set; }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder) => builder.ToTable("Orders", "Sales");
}

public sealed class OrderNote
{
    public int Id { get; set; }
}

/// <summary>
/// No parameterless constructor: EF Core's ApplyConfigurationsFromAssembly skips it, so OrderNote is
/// not in the model and the model's line must not name it.
/// </summary>
internal sealed class OrderNoteConfiguration(string table) : IEntityTypeConfiguration<OrderNote>
{
    public void Configure(EntityTypeBuilder<OrderNote> builder) => builder.ToTable(table);
}
