using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FusionModules.Analyzers;

/// <summary>
/// The host builds a web host that loads modules and never checks <c>HOSTINGSTARTUPASSEMBLIES</c>
/// against them (MOD0011).
/// </summary>
/// <remarks>
/// <para>
/// Two shapes load modules, because only <c>GenericWebHostBuilder</c> runs hosting startups:
/// <c>WebApplication.CreateBuilder</c>, and a generic host with <c>ConfigureWebHostDefaults</c> or
/// <c>ConfigureWebHost</c> on it. <c>CreateSlimBuilder</c>, <c>CreateEmptyBuilder</c> and
/// <c>Host.CreateApplicationBuilder</c> run none, so there is nothing to validate there.
/// </para>
/// <para>
/// Only where <c>KnownModulesGenerator</c> wrote <c>ValidateModules</c> into this compilation: a
/// host or a test assembly with the hosting types to hand and the list not switched off. A test
/// assembly creates hosts of its own for reasons of its own, so it is left alone.
/// </para>
/// <para>
/// And only in a host that references modules. Adding the package to a web project that has no
/// module yet is not a mistake, and a warning on its first build would say it is.
/// </para>
/// <para>
/// A builder counts as validated when a <c>ValidateModules</c> call in the same method receives it —
/// directly, further along a fluent chain, or through the local it was stored in. One the method
/// returns is someone else's to validate and is not reported.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HostValidationAnalyzer : DiagnosticAnalyzer
{
    private const string CreateBuilderMethodName = "CreateBuilder";
    private const string GenericHostBuilderExtensionsMetadataName = "Microsoft.Extensions.Hosting.GenericHostBuilderExtensions";
    private const string GenericHostWebHostBuilderExtensionsMetadataName = "Microsoft.Extensions.Hosting.GenericHostWebHostBuilderExtensions";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.HostDoesNotValidateModules];

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
        var extensions = compilation.Assembly.GetTypeByMetadataName(ModuleFacts.ValidateModulesClassMetadataName);

        if (extensions is null ||
            ModuleFacts.GetProjectKind(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions) == ProjectKind.Test ||
            !ModuleFacts.GetReferencedModules(
                compilation,
                compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName),
                ModuleFacts.GetModuleBaseType(compilation)).Any())
        {
            return;
        }

        var hosts = new HostShapes(compilation, extensions);

        context.RegisterOperationBlockStartAction(block =>
        {
            var builders = new BuildersInBlock();

            block.RegisterOperationAction(
                operationContext => builders.Record((IInvocationOperation)operationContext.Operation, hosts),
                OperationKind.Invocation);
            block.RegisterOperationAction(
                operationContext => builders.Record((IVariableDeclaratorOperation)operationContext.Operation),
                OperationKind.VariableDeclarator);
            block.RegisterOperationAction(
                operationContext => builders.Record((ISimpleAssignmentOperation)operationContext.Operation),
                OperationKind.SimpleAssignment);
            block.RegisterOperationAction(
                operationContext => builders.Record((IReturnOperation)operationContext.Operation),
                OperationKind.Return);

            block.RegisterOperationBlockEndAction(end =>
            {
                foreach (var (invocation, location) in builders.Unvalidated())
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.HostDoesNotValidateModules,
                        location,
                        invocation.TargetMethod.Name));
                }
            });
        });
    }

    /// <summary>The symbols that say what an invocation does to a host.</summary>
    private sealed class HostShapes(Compilation compilation, INamedTypeSymbol extensions)
    {
        private readonly INamedTypeSymbol? _webApplication = compilation.GetTypeByMetadataName(ModuleFacts.WebApplicationMetadataName);
        private readonly INamedTypeSymbol? _hostBuilder = compilation.GetTypeByMetadataName(ModuleFacts.HostBuilderMetadataName);
        private readonly INamedTypeSymbol? _webHostDefaults = compilation.GetTypeByMetadataName(GenericHostBuilderExtensionsMetadataName);
        private readonly INamedTypeSymbol? _webHost = compilation.GetTypeByMetadataName(GenericHostWebHostBuilderExtensionsMetadataName);

        public bool IsValidation(IMethodSymbol method) =>
            method.Name == ModuleFacts.ValidateModulesMethodName &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, extensions);

        /// <summary><c>WebApplication.CreateBuilder</c>: the builder it returns has run the modules.</summary>
        public bool IsWebApplicationBuilder(IMethodSymbol method) =>
            method.IsStatic &&
            method.Name == CreateBuilderMethodName &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, _webApplication);

        /// <summary><c>ConfigureWebHostDefaults</c> or <c>ConfigureWebHost</c> on a generic host.</summary>
        public bool IsWebHostOnGenericHost(IMethodSymbol method) =>
            method.IsExtensionMethod &&
            (SymbolEqualityComparer.Default.Equals(method.ContainingType, _webHostDefaults) ||
             SymbolEqualityComparer.Default.Equals(method.ContainingType, _webHost)) &&
            IsHostBuilder(method.Parameters.FirstOrDefault()?.Type);

        public bool IsHostBuilder(ITypeSymbol? type) =>
            type is not null &&
            _hostBuilder is not null &&
            (SymbolEqualityComparer.Default.Equals(type, _hostBuilder) || ModuleFacts.Implements(type, _hostBuilder));
    }

    /// <summary>
    /// What one method does with its builders. Callbacks for one block may run on different
    /// threads, hence the lock; everything is resolved once the block is complete.
    /// </summary>
    private sealed class BuildersInBlock
    {
        private readonly object _gate = new();
        private readonly List<(IInvocationOperation Invocation, IOperation Builder, Location Location)> _loading = [];
        private readonly List<IOperation> _validated = [];
        private readonly List<IOperation> _returned = [];
        private readonly Dictionary<ILocalSymbol, List<IOperation>> _assignments = new(SymbolEqualityComparer.Default);
        private HostShapes? _hosts;

        public void Record(IInvocationOperation invocation, HostShapes hosts)
        {
            var method = invocation.TargetMethod;

            lock (_gate)
            {
                _hosts = hosts;

                if (hosts.IsValidation(method))
                {
                    if (Receiver(invocation) is { } receiver)
                    {
                        _validated.Add(receiver);
                    }
                }
                else if (hosts.IsWebApplicationBuilder(method))
                {
                    _loading.Add((invocation, invocation, invocation.Syntax.GetLocation()));
                }
                else if (hosts.IsWebHostOnGenericHost(method) && Receiver(invocation) is { } hostBuilder)
                {
                    // On the method's name: the call usually spans a whole lambda.
                    var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                        ? access.Name.GetLocation()
                        : invocation.Syntax.GetLocation();

                    _loading.Add((invocation, hostBuilder, location));
                }
            }
        }

        public void Record(IVariableDeclaratorOperation declarator)
        {
            if (declarator.Initializer?.Value is { } value)
            {
                Assign(declarator.Symbol, value);
            }
        }

        public void Record(ISimpleAssignmentOperation assignment)
        {
            if (assignment.Target is ILocalReferenceOperation target)
            {
                Assign(target.Local, assignment.Value);
            }
        }

        public void Record(IReturnOperation @return)
        {
            if (@return.ReturnedValue is { } value)
            {
                lock (_gate)
                {
                    _returned.Add(value);
                }
            }
        }

        public List<(IInvocationOperation Invocation, Location Location)> Unvalidated()
        {
            lock (_gate)
            {
                var settled = new HashSet<object>(KeyComparer.Instance);
                foreach (var operation in _validated.Concat(_returned))
                {
                    settled.Add(Identity(operation));
                }

                return [.. _loading
                    .Where(loading => !settled.Contains(Identity(loading.Builder)))
                    .Select(loading => (loading.Invocation, loading.Location))];
            }
        }

        private void Assign(ILocalSymbol local, IOperation value)
        {
            lock (_gate)
            {
                if (!_assignments.TryGetValue(local, out var values))
                {
                    _assignments[local] = values = [];
                }

                values.Add(value);
            }
        }

        /// <summary>The builder an extension or instance call is made on.</summary>
        private static IOperation? Receiver(IInvocationOperation invocation) =>
            invocation.Instance ??
            (invocation.TargetMethod.IsExtensionMethod ? invocation.Arguments.FirstOrDefault()?.Value : null);

        /// <summary>
        /// What a builder-valued operation stands for: walked back along a fluent chain — each link
        /// returns the builder it was called on — and through a local assigned exactly once, to the
        /// call that created it. Anything else stands for itself.
        /// </summary>
        private object Identity(IOperation operation)
        {
            for (var depth = 0; depth < 64; depth++)
            {
                while (operation is IConversionOperation conversion)
                {
                    operation = conversion.Operand;
                }

                if (operation is ILocalReferenceOperation reference)
                {
                    if (!_assignments.TryGetValue(reference.Local, out var values) || values.Count != 1)
                    {
                        return reference.Local;
                    }

                    operation = values[0];
                    continue;
                }

                if (operation is IInvocationOperation invocation &&
                    Receiver(invocation) is { } receiver &&
                    _hosts is not null &&
                    _hosts.IsHostBuilder(invocation.Type) &&
                    _hosts.IsHostBuilder(receiver.Type))
                {
                    operation = receiver;
                    continue;
                }

                return operation;
            }

            return operation;
        }
    }

    /// <summary>Symbols by symbol equality, operations by reference.</summary>
    private sealed class KeyComparer : IEqualityComparer<object>
    {
        public static readonly KeyComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is ISymbol left && y is ISymbol right
                ? SymbolEqualityComparer.Default.Equals(left, right)
                : ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            obj is ISymbol symbol
                ? SymbolEqualityComparer.Default.GetHashCode(symbol)
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
