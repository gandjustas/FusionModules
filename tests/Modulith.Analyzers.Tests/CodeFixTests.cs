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

    [Test]
    public Task HubClient_GrantsAccessToTheProxyAssembly() =>
        // The fix worth reaching for: one line, and the module's surface stays exactly as MOD0001
        // wants it. The alternative makes the interface public and then the compiler drags its
        // whole signature closure out with it.
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            interface {|MOD0009:IPaymentClient|} { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """,
            """
            using Microsoft.AspNetCore.SignalR;
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]

            interface IPaymentClient { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """,
            codeActionIndex: 0);

    [Test]
    public Task HubClient_MakePublic_KeepsTheDocCommentAttached() =>
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            /// <summary>What the server pushes to a payment client.</summary>
            interface {|MOD0009:IPaymentClient|} { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """,
            """
            using Microsoft.AspNetCore.SignalR;

            /// <summary>What the server pushes to a payment client.</summary>
            public interface IPaymentClient { }

            sealed class PaymentHub : Hub<IPaymentClient> { }
            """,
            codeActionIndex: 1);

    [Test]
    public Task HubClient_MakePublic_TakesTheContainingTypeWithIt() =>
        // The case MOD0009 exists to report and the fix used to break: the interface is already
        // public, and what hides it from the proxy is the type it sits in. Making the interface
        // "public" again would emit `public public` and change nothing that mattered.
        //
        // The MOD0001 in the fixed state is the point rather than a wart. This is the spread the
        // rule's documentation promises, and it lands on the container — a different type, about
        // which there is now a real decision to take. The other fix costs one line and none of it.
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            class Contracts
            {
                public interface {|MOD0009:IPaymentClient|} { }
            }

            sealed class PaymentHub : Hub<Contracts.IPaymentClient> { }
            """,
            """
            using Microsoft.AspNetCore.SignalR;

            public class {|MOD0001:Contracts|}
            {
                public interface IPaymentClient { }
            }

            sealed class PaymentHub : Hub<Contracts.IPaymentClient> { }
            """,
            codeActionIndex: 1);

    [Test]
    public Task HubClient_GrantAccess_IsWrittenOnceForTheWholeFile() =>
        // Two clients, one attribute. Nothing in the provider guards against writing it twice,
        // and nothing needs to: BatchFixer drops the second insertion as conflicting with the
        // first, and once the attribute exists the analyzer stops reporting at all. The guarantee
        // is worth a test even though it costs no code — that is exactly the kind that rots.
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            interface {|MOD0009:IPaymentClient|} { }

            interface {|MOD0009:IRefundClient|} { }

            sealed class PaymentHub : Hub<IPaymentClient> { }

            sealed class RefundHub : Hub<IRefundClient> { }
            """,
            """
            using Microsoft.AspNetCore.SignalR;
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Microsoft.AspNetCore.SignalR.TypedClientBuilder")]

            interface IPaymentClient { }

            interface IRefundClient { }

            sealed class PaymentHub : Hub<IPaymentClient> { }

            sealed class RefundHub : Hub<IRefundClient> { }
            """,
            codeActionIndex: 0);

    private static Task VerifyFixAsync<TAnalyzer, TCodeFix>(
        string source, string fixedSource, int? codeActionIndex = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
            // A provider that offers more than one way out has to be told which one is under test.
            CodeActionIndex = codeActionIndex,
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
