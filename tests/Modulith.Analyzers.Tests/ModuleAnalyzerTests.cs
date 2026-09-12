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
