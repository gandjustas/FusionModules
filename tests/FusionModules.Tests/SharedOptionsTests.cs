using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tests;

/// <summary>
/// Two modules configuring the same options differently: in one process the options are one, and
/// each module's settings reach the others. Modelled on a migration whose services configured JSON,
/// SignalR and gRPC options each their own way.
/// </summary>
public class SharedOptionsTests
{
    [Test]
    public async Task TwoModulesConfiguringTheSameOptions_AreReported()
    {
        var result = await ModuleHost.StartAsync([new CamelCaseModule(), new SnakeCaseModule()]);

        await Assert.That(result.Failure).IsNull();
        var warning = result.Logged.SingleOrDefault(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains(nameof(SerializerSettings));
        await Assert.That(warning).Contains("(CamelCaseModule)");
        await Assert.That(warning).Contains("(SnakeCaseModule)");
    }

    [Test]
    public async Task AnInstanceThatIsNotAnOptionsDelegate_IsNotRead()
    {
        // Only the framework's ConfigureNamedOptions are looked into: a singleton of the application's
        // with a Name that throws, or one hiding another, is not this check's to call.
        var result = await ModuleHost.StartAsync([new InstanceModule()]);

        await Assert.That(result.Failure).IsNull();
    }

    [Test]
    public async Task TheFrameworksOwnOptions_AreReportedToo()
    {
        // JsonOptions is the framework's type; what is judged is who configures it, not where it lives.
        var result = await ModuleHost.StartAsync([new JsonModule(), new OtherJsonModule()]);

        await Assert.That(result.Logged).Contains(message => message.StartsWith("Options configured differently", StringComparison.Ordinal) && message.Contains("JsonOptions"));
    }

    [Test]
    public async Task OneModule_IsNotReported()
    {
        var result = await ModuleHost.StartAsync([new CamelCaseModule()]);

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    [Test]
    public async Task AHelperEveryModuleCalls_ConfiguresTheSameThing()
    {
        // A shared startup helper runs in every service: one delegate, the same settings.
        var result = await ModuleHost.StartAsync([new HelperModule(), new OtherHelperModule()]);

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    [Test]
    public async Task NamedOptions_AreHowModulesStayApart()
    {
        var result = await ModuleHost.StartAsync([new NamedModule("catalog"), new OtherNamedModule("ordering")]);

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    [Test]
    public async Task FrameworkRegistrations_AreNotJudged()
    {
        // Two modules calling AddControllers add the framework's own configuration, once.
        var result = await ModuleHost.StartAsync([new ControllersModule(), new OtherControllersModule()]);

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    [Test]
    public async Task ALibrarysDelegate_IsNotTheModulesSetting()
    {
        // One module calls AddSwaggerGen (Swashbuckle adds an MVC convention) and others a shared helper
        // adding a convention scoped to their own controllers; counted, it would read as every module
        // configuring MVC differently. Code a library declared runs the same for whoever calls it.
        var result = await ModuleHost.StartAsync([new CamelCaseModule(), new LibraryConfiguredModule()]);

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    private sealed class LibraryConfiguredModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.Configure(ForeignAssembly.Configure<SerializerSettings>(nameof(SerializerSettings.Naming), "snake_case"));
    }

    [Test]
    public async Task ListedInTheAllowList_IsNotReported()
    {
        var result = await ModuleHost.StartAsync([new CamelCaseModule(), new SnakeCaseModule()],
            settings: ("FusionModules:AllowedServiceCollisions", nameof(SerializerSettings)));

        await Assert.That(result.Logged).DoesNotContain(message => message.StartsWith("Options configured differently", StringComparison.Ordinal));
    }

    internal sealed class SerializerSettings
    {
        public string Naming { get; set; } = "";
    }

    internal class Named
    {
        public string Name => "base";
    }

    internal sealed class Hiding : Named
    {
        public new int Name => throw new InvalidOperationException("not to be called");
    }

    private sealed class InstanceModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddSingleton(new Hiding());
    }

    private sealed class CamelCaseModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.Configure<SerializerSettings>(options => options.Naming = "camelCase");
    }

    private sealed class SnakeCaseModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.Configure<SerializerSettings>(options => options.Naming = "snake_case");
    }

    private sealed class JsonModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.WriteIndented = true);
    }

    private sealed class OtherJsonModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.WriteIndented = false);
    }

    private static class SharedStartup
    {
        public static void Configure(IServiceCollection services) =>
            services.Configure<SerializerSettings>(options => options.Naming = "camelCase");
    }

    private class HelperModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            SharedStartup.Configure(builder.Services);
    }

    private sealed class OtherHelperModule : HelperModule;

    private class NamedModule(string name) : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.Configure<SerializerSettings>(name, options => options.Naming = name);
    }

    private sealed class OtherNamedModule(string name) : NamedModule(name);

    private class ControllersModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddControllers();
    }

    private sealed class OtherControllersModule : ControllersModule;
}
