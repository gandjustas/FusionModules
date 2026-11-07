using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FusionModules.Analyzers;

/// <summary>
/// What a module does rather than what it exposes: it must not replace the pipeline (MOD0008)
/// and must not register itself as a startup filter a second time (MOD0009).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleUsageAnalyzer : DiagnosticAnalyzer
{

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        Diagnostics.ModuleMustNotReplacePipeline,
        Diagnostics.RedundantStartupFilter,
    ];

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
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        var hostingStartupAttribute = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);

        if (!ModuleFacts.IsModuleCompilation(compilation, hostingStartupAttribute, moduleBase, context.CancellationToken))
        {
            return;
        }

        var webHostBuilder = compilation.GetTypeByMetadataName(ModuleFacts.WebHostBuilderMetadataName);
        var startupFilter = compilation.GetTypeByMetadataName(ModuleFacts.StartupFilterMetadataName);

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;

                CheckPipelineReplacement(operationContext, invocation, webHostBuilder);
                CheckRedundantStartupFilter(operationContext, invocation, startupFilter, moduleBase!);
            },
            OperationKind.Invocation);
    }

    // Configure and UseStartup do not add to the pipeline, they define it — discarding the host's
    // and every other module's.
    private static void CheckPipelineReplacement(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol? webHostBuilder)
    {
        if (webHostBuilder is null ||
            invocation.TargetMethod.Name is not ("Configure" or "UseStartup") ||
            !ReceiverIs(invocation, webHostBuilder))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.ModuleMustNotReplacePipeline,
            invocation.Syntax.GetLocation(),
            $"IWebHostBuilder.{invocation.TargetMethod.Name}"));
    }

    // ModuleBase already registers the module as a startup filter. Doing it again runs Configure
    // twice, which duplicates every endpoint the module maps, and a duplicate endpoint throws at
    // startup. Registering some other startup filter is ordinary and none of this rule's business.
    private static void CheckRedundantStartupFilter(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol? startupFilter,
        INamedTypeSymbol moduleBase)
    {
        var containingType = context.ContainingSymbol.ContainingType;
        if (containingType is null ||
            !ModuleFacts.InheritsFrom(containingType, moduleBase) ||
            !Operations.RegistersAsStartupFilter(invocation, startupFilter, containingType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.RedundantStartupFilter,
            invocation.Syntax.GetLocation(),
            containingType.Name));
    }

    private static bool ReceiverIs(IInvocationOperation invocation, INamedTypeSymbol type)
    {
        var receiver = Operations.Receiver(invocation)?.Type;

        return receiver is not null &&
            (SymbolEqualityComparer.Default.Equals(receiver, type) || ModuleFacts.Implements(receiver, type));
    }
}
