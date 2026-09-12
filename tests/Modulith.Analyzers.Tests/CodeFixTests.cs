using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Modulith.CodeFixes;

namespace Modulith.Analyzers.Tests;

/// <summary>
/// The fixes a migration leans on: MOD0001's Fix All turns a converted service's hundred public
/// types into one operation, and MOD0005's adds the attribute whose absence is otherwise silent.
/// </summary>
public class CodeFixTests
{
    [Test]
    public Task MakeInternal_RewritesOneType() =>
        VerifyFixAsync<ModuleAnalyzer, MakeTypeInternalCodeFixProvider>(
            """
            public class {|MOD0001:Leaked|} { }
            """,
            """
            internal class Leaked { }
            """);

    [Test]
    public Task MakeInternal_KeepsTheDocCommentAttached() =>
        // Removing and re-adding the modifier loses the leading trivia, which is a silent way to
        // delete every doc comment in a project during a Fix All.
        VerifyFixAsync<ModuleAnalyzer, MakeTypeInternalCodeFixProvider>(
            """
            /// <summary>An order.</summary>
            public class {|MOD0001:Order|} { }
            """,
            """
            /// <summary>An order.</summary>
            internal class Order { }
            """);

    [Test]
    public Task MakeInternal_HandlesEveryTypeKind() =>
        VerifyFixAsync<ModuleAnalyzer, MakeTypeInternalCodeFixProvider>(
            """
            public class {|MOD0001:A|} { }
            public struct {|MOD0001:B|} { }
            public interface {|MOD0001:C|} { }
            public enum {|MOD0001:D|} { One }
            public delegate void {|MOD0001:E|}();
            public record {|MOD0001:F|}(int Value);
            """,
            """
            internal class A { }
            internal struct B { }
            internal interface C { }
            internal enum D { One }
            internal delegate void E();
            internal record F(int Value);
            """);

    [Test]
    public Task MakeInternal_KeepsOtherModifiers() =>
        VerifyFixAsync<ModuleAnalyzer, MakeTypeInternalCodeFixProvider>(
            """
            public sealed partial class {|MOD0001:Leaked|} { }
            """,
            """
            internal sealed partial class Leaked { }
            """);

    [Test]
    public Task RegisterModule_AddsTheAttribute() =>
        VerifyFixAsync<ModuleAnalyzer, RegisterModuleCodeFixProvider>(
            """
            using Modulith;

            sealed class {|MOD0005:Orders|} : ModuleBase { }
            """,
            """
            using Modulith;
            [assembly: Microsoft.AspNetCore.Hosting.HostingStartup(typeof(Orders))]

            sealed class Orders : ModuleBase { }
            """);

    private static Task VerifyFixAsync<TAnalyzer, TCodeFix>(string source, string fixedSource)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        var framework = new ProjectState("Framework", LanguageNames.CSharp, "/Framework/", "cs");
        framework.Sources.Add(AnalyzerTest.FrameworkStubs);
        framework.Sources.Add(AnalyzerTest.ModuleBaseStub);

        test.TestState.AdditionalProjects.Add("Framework", framework);
        test.TestState.AdditionalProjectReferences.Add("Framework");
        test.TestState.Sources.Add(AnalyzerTest.RegisteredModule);
        test.TestState.Sources.Add(source);

        test.FixedState.Sources.Add(AnalyzerTest.RegisteredModule);
        test.FixedState.Sources.Add(fixedSource);

        return test.RunAsync(CancellationToken.None);
    }
}
