using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Modulith.Analyzers;

/// <summary>
/// What a module does rather than what it exposes: it must not replace the pipeline (MOD0006),
/// must not register itself twice (MOD0007), and should think twice about hosted services
/// (MOD0008).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleUsageAnalyzer : DiagnosticAnalyzer
{
    private const string WebHostBuilderMetadataName = "Microsoft.AspNetCore.Hosting.IWebHostBuilder";
    private const string StartupFilterMetadataName = "Microsoft.AspNetCore.Hosting.IStartupFilter";
    private const string HostedServiceMetadataName = "Microsoft.Extensions.Hosting.IHostedService";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        Diagnostics.ModuleMustNotReplacePipeline,
        Diagnostics.RedundantStartupFilter,
        Diagnostics.HostedServiceInModule,
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

        if (moduleBase is null ||
            hostingStartupAttribute is null ||
            compilation.GetEntryPoint(context.CancellationToken) is not null ||
            !ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartupAttribute, moduleBase))
        {
            return;
        }

        RegisterInvocationChecks(context, moduleBase);
    }

    private static void RegisterInvocationChecks(CompilationStartAnalysisContext context, INamedTypeSymbol moduleBase)
    {
        var compilation = context.Compilation;
        var webHostBuilder = compilation.GetTypeByMetadataName(WebHostBuilderMetadataName);
        var startupFilter = compilation.GetTypeByMetadataName(StartupFilterMetadataName);
        var hostedService = compilation.GetTypeByMetadataName(HostedServiceMetadataName);

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;

                CheckPipelineReplacement(operationContext, invocation, webHostBuilder);
                CheckRedundantStartupFilter(operationContext, invocation, startupFilter, moduleBase);
                CheckHostedService(operationContext, invocation, hostedService);
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

    // ModuleBase already does this. Doing it again runs Configure twice, which duplicates every
    // endpoint the module maps — and a duplicate endpoint throws at startup.
    private static void CheckRedundantStartupFilter(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol? startupFilter,
        INamedTypeSymbol moduleBase)
    {
        if (startupFilter is null ||
            !invocation.TargetMethod.Name.StartsWith("Add", StringComparison.Ordinal) ||
            !invocation.TargetMethod.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, startupFilter)))
        {
            return;
        }

        // Only inside a module: a host or a library registering a startup filter is ordinary.
        var containingType = context.ContainingSymbol.ContainingType;
        if (containingType is null || !ModuleFacts.InheritsFrom(containingType, moduleBase))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.RedundantStartupFilter,
            invocation.Syntax.GetLocation(),
            containingType.Name));
    }

    // A worker that ran in one replica of one service now runs wherever its module is loaded.
    private static void CheckHostedService(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol? hostedService)
    {
        if (hostedService is null || invocation.TargetMethod.Name is not "AddHostedService")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.HostedServiceInModule,
            invocation.Syntax.GetLocation(),
            context.Compilation.Assembly.Name));
    }

    private static bool ReceiverIs(IInvocationOperation invocation, INamedTypeSymbol type)
    {
        // An extension method's receiver is its first argument once reduced.
        var receiver = invocation.Instance?.Type
            ?? (invocation.TargetMethod.IsExtensionMethod ? invocation.Arguments.FirstOrDefault()?.Value.Type : null);

        return receiver is not null &&
            (SymbolEqualityComparer.Default.Equals(receiver, type) || ModuleFacts.Implements(receiver, type));
    }
}
