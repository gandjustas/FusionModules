using Microsoft.CodeAnalysis.Testing;

namespace Modulith.Analyzers.Tests;

public class HostAnalyzerTests
{
    private static DiagnosticResult Expect(string id, string assemblyName) =>
        new DiagnosticResult(id, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithNoLocation()
            .WithArguments(assemblyName);

    [Fact]
    public Task HostReferencingAModuleWithoutUsingIt_IsFine() =>
        // This is the arrangement the model depends on: the reference exists so the build orders
        // the projects and copies the module next to the host, and nothing more.
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            class Host
            {
                static void Main() { }
            }
            """);

    [Fact]
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

    [Fact]
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

    [Fact]
    public Task HostWithUnrelatedCompileErrors_IsNotReported() =>
        // With errors in the compilation the compiler cannot work out which references are used
        // and falls back to reporting all of them, so every module the host merely references
        // would be flagged — and the obvious fix would be to delete the references the model
        // needs. Fix the real error first; MOD0003 will have its say afterwards.
        AnalyzerTest.VerifyHostAsync<HostAnalyzer>("""
            class Host
            {
                static void Main()
                {
                    {|CS0246:Nonexistent|} x = null;
                }
            }
            """);

    [Fact]
    public Task LibraryUsingAModuleType_IsFine() =>
        // Only the host is held to this. A module may use another module's types — that is what
        // the runtime's reference check is for.
        AnalyzerTest.VerifyAsync<HostAnalyzer>("""
            class NotAHost
            {
                void Use() { }
            }
            """);
}
