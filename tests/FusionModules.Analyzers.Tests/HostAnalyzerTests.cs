using Microsoft.CodeAnalysis.Testing;

namespace FusionModules.Analyzers.Tests;

public class HostAnalyzerTests
{
    private static DiagnosticResult Expect(string id, string assemblyName) =>
        new DiagnosticResult(id, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithNoLocation()
            .WithArguments(assemblyName);

    [Test]
    public Task HostReferencingAModuleWithoutUsingIt_IsFine() =>
        // This is the arrangement the model depends on: the reference exists so the build orders
        // the projects and copies the module next to the host, and nothing more.
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            class Host
            {
                static void Main() { }
            }
            """);

    [Test]
    public Task HostUsingAModuleType_IsReported() =>
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            class Host
            {
                static void Main()
                {
                    _ = new OrderLeak();
                }
            }
            """,
            Expect(Diagnostics.HostMustNotUseModuleTypesId, "OrdersModule"));

    [Test]
    public Task HostDeclaringAnApplicationPartForAModule_IsReported() =>
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            using Microsoft.AspNetCore.Mvc.ApplicationParts;

            [assembly: ApplicationPart("OrdersModule")]

            class Host
            {
                static void Main() { }
            }
            """,
            Expect(Diagnostics.ApplicationPartMustNotNameModuleId, "OrdersModule"));

    [Test]
    public Task HostWithUnrelatedCompileErrors_IsNotReported() =>
        // With errors in the compilation the compiler cannot work out which references are used
        // and falls back to reporting all of them, so every module the host merely references
        // would be flagged — and the obvious fix would be to delete the references the model
        // needs. Fix the real error first; MOD0004 will have its say afterwards.
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            class Host
            {
                static void Main()
                {
                    {|CS0246:Nonexistent|} x = null;
                }
            }
            """);

    [Test]
    public Task HostImportingANamespaceAModuleAddsTo_IsFine() =>
        // An ordinary build runs without /doc, and then the compiler counts every assembly that
        // contributes a type to an imported namespace as used: this module, for its internal class
        // in Microsoft.Extensions.Hosting. The rule asks again with import tracking on.
        AnalyzerTest.VerifyHostInOrdinaryBuildAsync<HostAnalyzer>("""
            using Microsoft.Extensions.Hosting;

            class Host
            {
                static void Main() { }
            }
            """,
            """
            namespace Microsoft.Extensions.Hosting
            {
                internal static class OrdersHostingExtensions { }
            }
            """);

    [Test]
    public Task HostUsingAModuleTypeInAnOrdinaryBuild_IsReported() =>
        AnalyzerTest.VerifyHostInOrdinaryBuildAsync<HostAnalyzer>("""
            class Host
            {
                static void Main()
                {
                    _ = new OrderLeak();
                }
            }
            """,
            "",
            Expect(Diagnostics.HostMustNotUseModuleTypesId, "OrdersModule"));

    [Test]
    public Task HostUsingALibraryThatUsesAModule_IsLeftToTheLibrary() =>
        // The compiler counts the module as used by the host too, but the line to change is in
        // the library, which MOD0005 reports in its own build.
        AnalyzerTest.VerifyHostWithLibraryAsync<HostAnalyzer>("""
            public static class Pricing
            {
                public static object Quote() => new OrderLeak();
            }
            """,
            """
            class Host
            {
                static void Main()
                {
                    _ = Pricing.Quote();
                }
            }
            """);

    // MOD0005 — a library that is not a module keeps away from modules too.

    [Test]
    public Task LibraryUsingAModuleType_IsReported() =>
        AnalyzerTest.VerifyLibraryAsync<HostAnalyzer>("""
            public static class Pricing
            {
                public static object Quote() => new OrderLeak();
            }
            """,
            new DiagnosticResult(Diagnostics.LibraryMustNotUseModuleTypesId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithNoLocation()
                .WithArguments("TestProject", "OrdersModule"));

    [Test]
    public Task LibraryReferencingAModuleWithoutUsingIt_IsFine() =>
        AnalyzerTest.VerifyLibraryAsync<HostAnalyzer>("""
            public static class Pricing
            {
                public static int Quote() => 42;
            }
            """);

    [Test]
    public Task ModuleUsingAnotherModulesType_IsFine() =>
        // A module may use another module's types — that is what the runtime's reference check
        // is for, and what makes a composing module possible.
        AnalyzerTest.VerifyLibraryAsync<HostAnalyzer>(AnalyzerTest.RegisteredModule + """

            static class Composing
            {
                static object Use() => new OrderLeak();
            }
            """);

    [Test]
    public Task TestProjectUsingAModuleType_IsFine() =>
        AnalyzerTest.VerifyWithOptionAsync<HostAnalyzer>("build_property.FusionModulesProjectKind", "Test", """
            static class Arrange
            {
                static void Use() { }
            }
            """);
}
