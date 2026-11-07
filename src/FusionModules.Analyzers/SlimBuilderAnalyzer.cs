using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FusionModules.Analyzers;

/// <summary>
/// The host builds its application with a builder that never loads modules (MOD0006).
/// </summary>
/// <remarks>
/// <para>
/// <c>WebApplication.CreateSlimBuilder</c> and <c>CreateEmptyBuilder</c> configure their web host
/// through ASP.NET Core's <c>SlimWebHostBuilder</c>, which does not run hosting startups at all:
/// <c>HOSTINGSTARTUPASSEMBLIES</c> is read and then ignored. Every topology of such a host is the
/// host alone, with no error and no log line to say so.
/// </para>
/// <para>
/// A host only, as MSBuild classifies it, or a program without that classification. A test
/// project may build a slim host on purpose, and a library builds none of its own.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SlimBuilderAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The builders that skip hosting startups.</summary>
    internal static readonly ImmutableHashSet<string> SlimBuilderMethodNames = ["CreateSlimBuilder", "CreateEmptyBuilder"];

    /// <summary>The builder that runs them, and the one the code fix switches to.</summary>
    internal const string DefaultBuilderMethodName = "CreateBuilder";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.BuilderDoesNotLoadModules];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var webApplication = compilation.GetTypeByMetadataName(ModuleFacts.WebApplicationMetadataName);

        if (webApplication is null ||
            ModuleFacts.GetModuleBaseType(compilation) is null ||
            ModuleFacts.GetProjectKind(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions) switch
            {
                ProjectKind.Host => false,
                ProjectKind.Unknown => compilation.GetEntryPoint(context.CancellationToken) is null,
                _ => true,
            })
        {
            return;
        }

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var method = invocation.TargetMethod;

                if (!method.IsStatic ||
                    !SlimBuilderMethodNames.Contains(method.Name) ||
                    !SymbolEqualityComparer.Default.Equals(method.ContainingType, webApplication))
                {
                    return;
                }

                operationContext.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.BuilderDoesNotLoadModules,
                    NameLocation(invocation),
                    method.Name));
            },
            OperationKind.Invocation);
    }

    /// <summary>The method's name: <c>WebApplication.CreateSlimBuilder</c>, or a bare one under <c>using static</c>.</summary>
    private static Location NameLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax syntax
            ? syntax.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.GetLocation(),
                SimpleNameSyntax name => name.GetLocation(),
                _ => syntax.GetLocation(),
            }
            : invocation.Syntax.GetLocation();
}
