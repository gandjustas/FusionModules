using static FusionModules.Analyzers.Tests.RegistrationTest;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0014: two modules registering one service with different implementations, found by the host's
/// build from what each module's build recorded.
/// </summary>
public class ModuleRegistrationsTests
{
    private const string Contracts = """
        namespace Shop
        {
            public interface IChecker { }

            public interface IRepository<T> { }

            [MultipleImplementations]
            public interface IStatusListener { }

            // Declared by the contracts library itself, as the runtime check expects: matched by name.
            [System.AttributeUsage(System.AttributeTargets.Interface)]
            internal sealed class MultipleImplementationsAttribute : System.Attribute { }

            public sealed class SharedChecker : IChecker { }
        }
        """;

    private static Module Registering(string name, string body, string types = "", string assembly = "") => new(name, $$"""
        {{assembly}}

        namespace Shop
        {
            {{types}}

            static class Registrations
            {
                public static void Register(IServiceCollection services)
                {
                    {{body}}
                }
            }
        }
        """);

    // The generator: what a module records.

    [Test]
    public async Task RecordsAServiceWithItsImplementation()
    {
        var generated = await GenerateAsync("""
            namespace Shop
            {
                sealed class OrdersChecker : IChecker { }

                static class Registrations
                {
                    public static void Register(IServiceCollection services) => services.AddSingleton<IChecker, OrdersChecker>();
                }
            }
            """, Contracts);

        await Assert.That(generated).Contains("""("Shop.IChecker", "Shop.IChecker, Contracts", "Shop.OrdersChecker", "Shop.OrdersChecker, Orders", "IChecker|Shop.IChecker")]""");
        await Assert.That(generated).Contains("internal sealed class ModuleRegistrationAttribute");
        await Assert.That(generated).Contains("namespace FusionModules.Generated.Orders");
    }

    [Test]
    public async Task AModuleNamedLikeAKeyword_StillCompiles()
    {
        // The namespace is the assembly name: an assembly called "event" needs it escaped.
        var generated = await GenerateAsync("""
            namespace Shop
            {
                sealed class OrdersChecker : IChecker { }

                static class Registrations
                {
                    public static void Register(IServiceCollection services) => services.AddSingleton<IChecker, OrdersChecker>();
                }
            }
            """, Contracts, assemblyName: "event");

        await Assert.That(generated).Contains("namespace FusionModules.Generated.@event");
    }

    [Test]
    public async Task RecordsEveryWayOfNamingTheImplementation()
    {
        var generated = await GenerateAsync("""
            namespace Shop
            {
                sealed class OrdersChecker : IChecker { }
                sealed class Repository<T> : IRepository<T> { }
                sealed class Cache { }

                static class Registrations
                {
                    public static void Register(IServiceCollection services)
                    {
                        services.AddSingleton<Cache>();
                        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
                        services.AddSingleton<IChecker>(sp => new OrdersChecker());
                        services.AddSingleton(typeof(IChecker), new OrdersChecker());
                    }
                }
            }
            """, Contracts);

        // The type itself, a generic definition, a factory, an instance.
        await Assert.That(generated).Contains("""("Shop.Cache", "Shop.Cache, Orders", "Shop.Cache", "Shop.Cache, Orders", """);
        await Assert.That(generated).Contains("""("Shop.IRepository<>", "Shop.IRepository`1, Contracts", "Shop.Repository<>", "Shop.Repository`1, Orders", "IRepository<>|Shop.IRepository<>|Shop.IRepository`1")""");
        await Assert.That(generated).Contains("""(factory in Orders returning Shop.OrdersChecker)""");
        await Assert.That(generated).Contains("""("Shop.IChecker", "Shop.IChecker, Contracts", "Shop.OrdersChecker", "Shop.OrdersChecker, Orders", """);
    }

    [Test]
    public async Task LeavesOutWhatIsASequenceOrAnotherServiceByDesign()
    {
        var generated = await GenerateAsync("""
            namespace Shop
            {
                sealed class OrdersChecker : IChecker { }
                sealed class OrdersListener : IStatusListener { }
                sealed class Worker : Microsoft.Extensions.Hosting.IHostedService { }

                static class Registrations
                {
                    public static void Register(IServiceCollection services)
                    {
                        services.AddKeyedSingleton<IChecker, OrdersChecker>("orders");
                        services.AddSingleton<IStatusListener, OrdersListener>();
                        services.AddHostedService<Worker>();
                        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChecker, OrdersChecker>());
                    }

                    public static void Generic<T>(IServiceCollection services) where T : class => services.AddSingleton<T>();
                }
            }
            """, Contracts);

        // Keyed, marked as a sequence, the framework's own contract, added as a sequence, a
        // helper's type parameter: nothing to record, so no file.
        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task GeneratesNothingOutsideAModule()
    {
        const string source = """
            namespace Shop
            {
                sealed class OrdersChecker : IChecker { }

                static class Registrations
                {
                    public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
                        Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions.AddSingleton<IChecker, OrdersChecker>(services);
                }
            }
            """;

        await Assert.That(await GenerateAsync(source, Contracts, registered: false)).IsNull();
        await Assert.That(await GenerateAsync(source, Contracts, projectKind: "Host")).IsNull();
    }

    // The host: what it makes of the records.

    [Test]
    public async Task ReportsTwoModulesRegisteringOneServiceDifferently()
    {
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker, OrdersChecker>();", "sealed class OrdersChecker : IChecker { }"),
                Registering("Billing", "services.AddSingleton<IChecker, BillingChecker>();", "sealed class BillingChecker : IChecker { }"),
            ],
            Contracts);

        await Assert.That(diagnostics).HasSingleItem();
        var message = diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("'Shop.IChecker'");
        await Assert.That(message).Contains("Billing: Shop.BillingChecker; Orders: Shop.OrdersChecker");
    }

    [Test]
    public async Task RecordsATypedHttpClient()
    {
        // The usual way a remote implementation of a contract is registered; a rule that missed it
        // would miss the transport most likely to collide. The configure delegate is the client's,
        // not a factory of the service.
        var generated = await GenerateAsync("""
            namespace Shop
            {
                sealed class RestChecker : IChecker { }
                sealed class Plain { }

                static class Registrations
                {
                    public static void Register(IServiceCollection services)
                    {
                        services.AddHttpClient<IChecker, RestChecker>(client => client.BaseAddress = new System.Uri("http://rest"));
                        services.AddHttpClient<Plain>();
                        services.AddHttpClient("named");
                    }
                }
            }
            """, Contracts);

        await Assert.That(generated).Contains("""("Shop.IChecker", "Shop.IChecker, Contracts", "Shop.RestChecker", "Shop.RestChecker, Orders", """);
        await Assert.That(generated).Contains("""("Shop.Plain", "Shop.Plain, Orders", "Shop.Plain", "Shop.Plain, Orders", """);
    }

    [Test]
    public async Task ATypedHttpClientCollides_LikeAnyRegistration()
    {
        var diagnostics = await HostAsync(
            [
                Registering("Local", "services.AddTransient<IChecker, LocalChecker>();", "sealed class LocalChecker : IChecker { }"),
                Registering("Rest", "services.AddHttpClient<IChecker, RestChecker>(client => { });", "sealed class RestChecker : IChecker { }"),
            ],
            Contracts);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("Local: Shop.LocalChecker; Rest: Shop.RestChecker");
    }

    [Test]
    public async Task ReportsTryAddToo()
    {
        // First wins instead of last; one module still gets the other's implementation, and the
        // registration that loses is one the runtime check never sees.
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.TryAddSingleton<IChecker, OrdersChecker>();", "sealed class OrdersChecker : IChecker { }"),
                Registering("Billing", "services.TryAddSingleton<IChecker, BillingChecker>();", "sealed class BillingChecker : IChecker { }"),
            ],
            Contracts);

        await Assert.That(diagnostics).HasSingleItem();
    }

    [Test]
    public async Task ReadsTheRecordsOutOfReferenceAssemblies()
    {
        // What a build with ProduceReferenceAssembly hands the host: internal types and private
        // members dropped. The attribute is internal and has to survive it.
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker, OrdersChecker>();", "sealed class OrdersChecker : IChecker { }"),
                Registering("Billing", "services.AddSingleton<IChecker, BillingChecker>();", "sealed class BillingChecker : IChecker { }"),
            ],
            Contracts,
            metadataOnly: true);

        await Assert.That(diagnostics).HasSingleItem();
    }

    [Test]
    public async Task TheSameImplementationIsNotACollision()
    {
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker, SharedChecker>();"),
                Registering("Billing", "services.AddSingleton<IChecker, SharedChecker>();"),
            ],
            Contracts);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task NamesakesInTwoModulesAreTwoServices()
    {
        // Each module declares its own internal Shop.ILocalChecker: one name, two types, and the
        // runtime keys the container by type.
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<ILocalChecker, Checker>();", "interface ILocalChecker { } sealed class Checker : ILocalChecker { }"),
                Registering("Billing", "services.AddSingleton<ILocalChecker, OtherChecker>();", "interface ILocalChecker { } sealed class OtherChecker : ILocalChecker { }"),
            ],
            Contracts);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task TwoFactoriesAreTwoImplementations()
    {
        // Each builds the type its own way, which is what the runtime check concludes too.
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker>(sp => new SharedChecker());"),
                Registering("Billing", "services.AddSingleton<IChecker>(sp => new SharedChecker());"),
            ],
            Contracts);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("Billing: (factory in Billing returning Shop.SharedChecker); Orders: (factory in Orders returning Shop.SharedChecker)");
    }

    [Test]
    [Arguments("fusion_modules_allowed_service_collisions")]
    [Arguments("build_property.fusion_modules_allowed_service_collisions")]
    public async Task AlternativesListedInTheOptionAreNotReported(string key)
    {
        // Two modules that no topology loads together — a real gateway and a fake one, say.
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker, OrdersChecker>();", "sealed class OrdersChecker : IChecker { }"),
                Registering("Billing", "services.AddSingleton<IChecker, BillingChecker>();", "sealed class BillingChecker : IChecker { }"),
            ],
            Contracts,
            new() { [key] = "Other.Thing; IChecker" });

        await Assert.That(diagnostics).IsEmpty();
    }

    // The option's names: the same spellings the runtime's AllowedServiceCollisions accepts, and the
    // same sources. A type named one way in configuration and another in .editorconfig is one name
    // too many; the shared cases are compiled into both test projects to keep the two in step.

    private const string AllowedCollisions = "fusion_modules_allowed_service_collisions";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> TwoModulesRegisterAsync(string service, string? allowed, string? property = null)
    {
        var registration = $"services.AddSingleton<{service}>(sp => new {service}());";
        var options = new Dictionary<string, string>();
        if (allowed is not null)
        {
            options[AllowedCollisions] = allowed;
        }

        if (property is not null)
        {
            options[$"build_property.{AllowedCollisions}"] = property;
        }

        return HostAsync([Registering("Orders", registration), Registering("Billing", registration)], Contracts + FusionModules.Tests.Shared.TypeSpellingCases.Source, options);
    }

    [Test]
    public async Task TheSpellingCases_CollideWhenNothingIsListed()
    {
        // Without this the tests below would pass for a registration MOD0014 never saw.
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Worker<Shop.Order>", null)).HasSingleItem();
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Pair<Shop.Order, Shop.Order>", null)).HasSingleItem();
        await Assert.That(await TwoModulesRegisterAsync("Shop.Box<Shop.Order>.Item", null)).HasSingleItem();
    }

    public static IEnumerable<Func<string>> ItemOfBox() =>
        FusionModules.Tests.Shared.TypeSpellingCases.ItemOfBox.Select(spelling => new Func<string>(() => spelling));

    [Test]
    [MethodDataSource(nameof(ItemOfBox))]
    public async Task TheOption_NamesATypeNestedInAGenericAsTheRuntimeDoes(string spelling) =>
        await Assert.That(await TwoModulesRegisterAsync("Shop.Box<Shop.Order>.Item", spelling)).IsEmpty();

    public static IEnumerable<Func<string>> WorkerOfOrder() =>
        FusionModules.Tests.Shared.TypeSpellingCases.WorkerOfOrder.Select(spelling => new Func<string>(() => spelling));

    [Test]
    [MethodDataSource(nameof(WorkerOfOrder))]
    public async Task TheOption_AcceptsTheRuntimesSpellings(string spelling) =>
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Worker<Shop.Order>", spelling)).IsEmpty();

    public static IEnumerable<Func<string>> PairOfOrders() =>
        FusionModules.Tests.Shared.TypeSpellingCases.PairOfOrders.Select(spelling => new Func<string>(() => spelling));

    [Test]
    [MethodDataSource(nameof(PairOfOrders))]
    public async Task TheOption_TakesATwoArgumentGenericAsOneEntry(string spelling) =>
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Pair<Shop.Order, Shop.Order>", spelling)).IsEmpty();

    [Test]
    public async Task TheOption_SeparatesNamesAtACommaOutsideBrackets()
    {
        var both = FusionModules.Tests.Shared.TypeSpellingCases.BothWorkers;

        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Worker<Shop.Order>", both)).IsEmpty();
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Pair<Shop.Order, Shop.Order>", both)).IsEmpty();
    }

    [Test]
    public async Task AnEmptyMsBuildProperty_DoesNotHideTheEditorConfigEntry() =>
        // The package declares the list as a CompilerVisibleProperty, so build_property.<key> is
        // always present — empty unless the project sets it. Reading only the first source found
        // would let that empty value win over what the user wrote in .editorconfig.
        await Assert.That(await TwoModulesRegisterAsync("Shop.Outer.Worker<Shop.Order>", "Worker<>", property: "")).IsEmpty();

    [Test]
    public async Task ATestProjectIsNotAHost()
    {
        var diagnostics = await HostAsync(
            [
                Registering("Orders", "services.AddSingleton<IChecker, OrdersChecker>();", "sealed class OrdersChecker : IChecker { }"),
                Registering("Billing", "services.AddSingleton<IChecker, BillingChecker>();", "sealed class BillingChecker : IChecker { }"),
            ],
            Contracts,
            hostKind: "Test");

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task AModuleThatSeesAnothersInternalsStillCompilesCleanly()
    {
        // Orders lets Billing see its internals, so Billing sees Orders' generated attribute as
        // well as its own. One namespace per assembly keeps that from being CS0436 — which the
        // harness turns into a failure.
        var diagnostics = await HostAsync(
            [
                Registering(
                    "Orders",
                    "services.AddSingleton<IChecker, OrdersChecker>();",
                    "sealed class OrdersChecker : IChecker { } public static class OrdersMarker { }",
                    """[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Billing")]"""),
                Registering("Billing", "services.AddSingleton<IChecker, OrdersChecker>(); _ = typeof(OrdersMarker);") with { References = ["Orders"] },
            ],
            Contracts);

        await Assert.That(diagnostics).IsEmpty();
    }
}
