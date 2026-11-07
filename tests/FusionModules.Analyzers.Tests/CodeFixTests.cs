using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using FusionModules.CodeFixes;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// The fixes a migration leans on: MOD0001's Fix All turns a converted service's hundred public
/// types into one operation, and MOD0003's adds the attribute whose absence is otherwise silent.
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
            using FusionModules;

            sealed class {|MOD0003:Orders|} : ModuleBase { }
            """,
            """
            using FusionModules;
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

            interface {|MOD0010:IPaymentClient|} { }

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
            interface {|MOD0010:IPaymentClient|} { }

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
        // The case MOD0010 exists to report and the fix used to break: the interface is already
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
                public interface {|MOD0010:IPaymentClient|} { }
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
    public Task HubClient_MakePublic_ChangesTheWholeChainInOneGo() =>
        // Both the interface and its container hide it. Rewriting the container from its original
        // node would drop the interface's own change, and the fix would take two rounds.
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            class Contracts
            {
                internal interface {|MOD0010:IPaymentClient|} { }
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
    public Task RegisterModule_QualifiesATypeInANamespace() =>
        // The attribute sits above the namespace, so the type has to be named in full there.
        VerifyFixAsync<ModuleAnalyzer, RegisterModuleCodeFixProvider>(
            """
            using FusionModules;

            namespace Orders.Nested
            {
                sealed class {|MOD0003:Outer|} : ModuleBase { }
            }
            """,
            """
            using FusionModules;
            [assembly: Microsoft.AspNetCore.Hosting.HostingStartup(typeof(Orders.Nested.Outer))]

            namespace Orders.Nested
            {
                sealed class Outer : ModuleBase { }
            }
            """);

    [Test]
    public Task ValidateModules_IsCalledWhereTheBuilderIsCreated() =>
        VerifyFixAsync<HostValidationAnalyzer, ValidateModulesCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = {|MOD0012:WebApplication.CreateBuilder(args)|};
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args).ValidateModules();
                }
            }
            """,
            generated: AnalyzerTest.GeneratedForAHost);

    [Test]
    public Task ValidateModules_FollowsTheWebHostOnAGenericHost() =>
        VerifyFixAsync<HostValidationAnalyzer, ValidateModulesCodeFixProvider>(
            """
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var host = Host.CreateDefaultBuilder(args)
                        .{|MOD0012:ConfigureWebHostDefaults|}(web => { });
                }
            }
            """,
            """
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var host = Host.CreateDefaultBuilder(args)
                        .ConfigureWebHostDefaults(web => { }).ValidateModules();
                }
            }
            """,
            generated: AnalyzerTest.GeneratedForAHost);

    [Test]
    public Task SlimBuilder_BecomesTheDefaultBuilder() =>
        VerifyFixAsync<SlimBuilderAnalyzer, UseCreateBuilderCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.{|MOD0006:CreateSlimBuilder|}(args);
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                }
            }
            """,
            projectKind: "Host");

    [Test]
    public Task EmptyBuilder_BecomesTheDefaultBuilderWithTheSameOptions() =>
        VerifyFixAsync<SlimBuilderAnalyzer, UseCreateBuilderCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.{|MOD0006:CreateEmptyBuilder|}(new WebApplicationOptions { Args = args });
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
                }
            }
            """,
            projectKind: "Host");

    [Test]
    public Task ValidateModules_BringsItsUsingWhenItIsNotInScope() =>
        // A host with implicit usings turned off does not have the builder's namespace.
        VerifyFixAsync<HostValidationAnalyzer, ValidateModulesCodeFixProvider>(
            """
            using Microsoft.AspNetCore.Builder;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = {|MOD0012:WebApplication.CreateBuilder(args)|};
                }
            }
            """,
            """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Hosting;

            class Program
            {
                static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args).ValidateModules();
                }
            }
            """,
            generated: AnalyzerTest.GeneratedForAHost);

    [Test]
    public Task HubClient_GrantAccess_IsWrittenOnceForTheWholeFile() =>
        // Two clients, one attribute. Nothing in the provider guards against writing it twice,
        // and nothing needs to: BatchFixer drops the second insertion as conflicting with the
        // first, and once the attribute exists the analyzer stops reporting at all. The guarantee
        // is worth a test even though it costs no code — that is exactly the kind that rots.
        VerifyFixAsync<ModuleAnalyzer, HubClientReachableCodeFixProvider>(
            """
            using Microsoft.AspNetCore.SignalR;

            interface {|MOD0010:IPaymentClient|} { }

            interface {|MOD0010:IRefundClient|} { }

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

    private static async Task VerifyFixAsync<TAnalyzer, TCodeFix>(
        string source, string fixedSource, int? codeActionIndex = null, string? generated = null, string? projectKind = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
            // A provider that offers more than one way out has to be told which one is under test.
            CodeActionIndex = codeActionIndex,
        };

        // The generated host code comes with a host.
        projectKind ??= generated is null ? null : "Host";

        string[] shared = generated is null ? [AnalyzerTest.RegisteredModule] : [AnalyzerTest.RegisteredModule, generated];
        await AnalyzerTest.ArrangeAsync(test.TestState, withModuleBase: true, [.. shared, source]);
        await AnalyzerTest.ArrangeAsync(test.FixedState, withModuleBase: true, [.. shared, fixedSource]);

        // The generated host code comes with a host, and a host with modules references them.
        if (generated is not null || projectKind == "Host")
        {
            test.TestState.AdditionalReferences.Add(await AnalyzerTest.ModuleReference.Value);
            test.FixedState.AdditionalReferences.Add(await AnalyzerTest.ModuleReference.Value);
        }

        if (projectKind is not null)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", $"""
                is_global = true
                build_property.FusionModulesProjectKind = {projectKind}
                """));
        }

        await test.RunAsync(CancellationToken.None);
    }
}
