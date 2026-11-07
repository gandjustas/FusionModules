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
    private const string WebHostBuilderMetadataName = "Microsoft.AspNetCore.Hosting.IWebHostBuilder";
    private const string StartupFilterMetadataName = "Microsoft.AspNetCore.Hosting.IStartupFilter";

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

        var webHostBuilder = compilation.GetTypeByMetadataName(WebHostBuilderMetadataName);
        var startupFilter = compilation.GetTypeByMetadataName(StartupFilterMetadataName);

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
        if (startupFilter is null ||
            !invocation.TargetMethod.Name.StartsWith("Add", StringComparison.Ordinal) ||
            !invocation.TargetMethod.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, startupFilter)))
        {
            return;
        }

        var containingType = context.ContainingSymbol.ContainingType;
        if (containingType is null ||
            !ModuleFacts.InheritsFrom(containingType, moduleBase) ||
            !RegistersItself(invocation, containingType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.RedundantStartupFilter,
            invocation.Syntax.GetLocation(),
            containingType.Name));
    }

    // AddSingleton<IStartupFilter>(this), AddSingleton<IStartupFilter>(new Module()) or
    // AddSingleton<IStartupFilter, Module>(): each puts the module in the list a second time.
    private static bool RegistersItself(IInvocationOperation invocation, INamedTypeSymbol module) =>
        invocation.TargetMethod.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, module)) ||
        invocation.Arguments.Any(argument => Unwrap(argument.Value) switch
        {
            IInstanceReferenceOperation => true,
            IObjectCreationOperation creation => SymbolEqualityComparer.Default.Equals(creation.Type, module),
            _ => false,
        });

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
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
