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
            !ModuleFacts.IsHost(compilation, context.Options.AnalyzerConfigOptionsProvider.GlobalOptions, context.CancellationToken) ||
            // A host with no module to load is not missing any: a service that only takes the
            // package along — or never had a module yet — builds clean, as it does under MOD0012.
            !ModuleFacts.GetReferencedModules(
                compilation,
                compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName),
                ModuleFacts.GetModuleBaseType(compilation)).Any())
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
                    Operations.NameLocation(invocation),
                    method.Name));
            },
            OperationKind.Invocation);
    }
}
