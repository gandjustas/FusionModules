namespace Modulith.Analyzers.Tests;

public class ModuleAnalyzerTests
{
    // MOD0001 — a module keeps its types to itself.

    [Test]
    public Task PublicClassInModule_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class {|MOD0001:Leaked|} { }
            """);

    [Test]
    public Task InternalClassInModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            internal class NotLeaked { }
            """);

    // The demo analyzer only looked at TypeKind.Class, so a public record struct used as a
    // message contract slipped through. Every kind counts.
    [Test]
    [Arguments("public struct {|MOD0001:Leaked|} { }")]
    [Arguments("public readonly record struct {|MOD0001:Leaked|}(int Value);")]
    [Arguments("public record {|MOD0001:Leaked|}(int Value);")]
    [Arguments("public interface {|MOD0001:ILeaked|} { }")]
    [Arguments("public enum {|MOD0001:Leaked|} { One }")]
    [Arguments("public delegate void {|MOD0001:Leaked|}();")]
    public Task PublicTypeOfAnyKind_IsReported(string source) =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>(source);

    [Test]
    public Task PublicTypeInNonModuleAssembly_IsFine() =>
        // No [assembly: HostingStartup], so this is an ordinary library — a contracts assembly,
        // for instance — and public is exactly what its types should be.
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            public class Contract { }
            """);

    [Test]
    public Task ControllerInModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase { }
            """);

    [Test]
    public Task PageModelInModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class IndexModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { }
            """);

    [Test]
    public Task TagHelperInModule_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class EmailTagHelper : Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper { }
            """);

    [Test]
    public Task ConfiguredEntityInModule_IsFine() =>
        // EF Core reaches the entity by reflection, and the configuration in the same assembly is
        // what proves this type is an entity rather than a leak.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class Order { }

            internal class OrderConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Order> { }
            """);

    [Test]
    public Task EntityWithoutConfiguration_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            public class {|MOD0001:Order|} { }
            """);

    [Test]
    public Task TypeOnTheEditorConfigAllowList_IsFine() =>
        // Real solutions have public types the framework needs but the analyzer cannot infer:
        // types bound by a source-generated JsonSerializerContext in another assembly, say.
        AnalyzerTest.VerifyWithOptionAsync<ModuleAnalyzer>("modulith_allowed_public_types", "OrderDto", """
            public class OrderDto { }
            """);

    [Test]
    public Task TypeNotOnTheEditorConfigAllowList_IsReported() =>
        AnalyzerTest.VerifyWithOptionAsync<ModuleAnalyzer>("modulith_allowed_public_types", "SomethingElse", """
            public class {|MOD0001:OrderDto|} { }
            """);

    // MOD0002 — the type named by the attribute really is a module.

    [Test]
    public Task HostingStartupNamingANonModule_IsReported() =>
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;

            [assembly: HostingStartup(typeof({|MOD0002:NotAModule|}))]

            class NotAModule { }
            """);

    [Test]
    public Task HostingStartupNamingAModule_IsFine() =>
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>(AnalyzerTest.RegisteredModule);

    [Test]
    public Task ModuleDerivedIndirectly_IsFine() =>
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Modulith;

            [assembly: HostingStartup(typeof(ConcreteModule))]

            abstract class CompanyModule : ModuleBase { }

            sealed class ConcreteModule : CompanyModule { }
            """);

    // MOD0005 — every module is registered. The other half of MOD0002.

    [Test]
    public Task UnregisteredModule_IsReported() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Modulith;

            sealed class {|MOD0005:Forgotten|} : ModuleBase { }
            """);

    [Test]
    public Task ModuleAssemblyWithNoAttributeAtAll_IsReported() =>
        // The whole-assembly version of the same mistake: somebody wrote a module and never
        // registered it, so nothing in the assembly runs and nothing says so.
        AnalyzerTest.VerifyAsync<ModuleAnalyzer>("""
            using Modulith;

            sealed class {|MOD0005:OrdersModule|} : ModuleBase { }
            """);

    [Test]
    public Task AbstractModuleBase_IsNotReported() =>
        // A shared base for a company's modules is not itself a module.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Modulith;

            abstract class CompanyModule : ModuleBase { }
            """);

    [Test]
    public Task SecondRegisteredModuleInSameAssembly_IsFine() =>
        // HostingStartupAttribute allows multiple, so one assembly may carry several modules.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;
            using Modulith;

            [assembly: HostingStartup(typeof(SecondModule))]

            sealed class SecondModule : ModuleBase { }
            """);

    // MOD0009 — SignalR's proxy has to be able to implement the hub's client interface.

    [Test]
    public Task HubWithInternalClientInterface_IsReported() =>
        // The failure this replaces is silent: clean build, no other rule, green tests, and a
        // TypeLoadException the first time a client connects.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            interface {|MOD0009:IPaymentClient|}
            {
                System.Threading.Tasks.Task Paid(string reference);
            }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task HubWithPublicClientInterface_IsFine() =>
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            public interface IPaymentClient
            {
                System.Threading.Tasks.Task Paid(string reference);
            }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task PublicHubClientInterface_IsExemptFromMod0001() =>
        // Otherwise the two rules argue over the same line: one demands public, the other internal.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            public interface IPaymentClient { }

            public class PaymentHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task InternalsVisibleToTypedClientBuilder_SilencesTheRule() =>
        // The recommended fix, and the reason this rule can be an error: one line keeps the whole
        // module internal, because the proxy's assembly is unsigned and named at runtime.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]

            interface IPaymentClient { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task InternalsVisibleToWithPublicKey_SilencesTheRule() =>
        // A signed friend name carries the key after a comma; only the simple name is the name.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder, PublicKey=0024000004800000")]

            interface IPaymentClient { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task NonGenericHub_IsFine() =>
        // No client interface, nothing to generate, nothing to reach.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            sealed class PaymentHub : Hub { }
            """);

    [Test]
    public Task HubItself_IsExemptFromMod0001() =>
        // SignalR resolves a hub by reflection, exactly as MVC resolves a controller.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            public sealed class PaymentHub : Hub { }
            """);

    [Test]
    public Task TwoHubsSharingOneClientInterface_ReportOnce() =>
        // The rule reports on the interface, which there is one of, not on each hub.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            interface {|MOD0009:IPaymentClient|} { }

            sealed class PaymentHub : Hub<IPaymentClient> { }

            sealed class RefundHub : Hub<IPaymentClient> { }
            """);

    [Test]
    public Task PublicClientInterfaceNestedInAnInternalType_IsReported() =>
        // Public inside internal is invisible outside the assembly, which is the only question
        // the proxy in another assembly asks.
        AnalyzerTest.VerifyModuleAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.SignalR;

            class Contracts
            {
                public interface {|MOD0009:IPaymentClient|} { }
            }

            sealed class PaymentHub : Hub<Contracts.IPaymentClient> { }
            """);

    // MOD0020 — say what is actually wrong when the runtime package is missing.

    [Test]
    public Task HostingStartupWithoutTheRuntimePackage_ReportsMissingPackage() =>
        // The demo analyzer dereferenced the absent ModuleBase symbol here and crashed. Even
        // reporting "does not derive from ModuleBase" would be unhelpful: the type is not there.
        AnalyzerTest.VerifyWithoutModuleBaseAsync<ModuleAnalyzer>("""
            using Microsoft.AspNetCore.Hosting;

            [assembly: {|MOD0020:HostingStartup(typeof(Orphan))|}]

            class Orphan { }
            """);
}
