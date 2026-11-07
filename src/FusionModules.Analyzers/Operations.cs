using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace FusionModules.Analyzers;

/// <summary>
/// The questions more than one rule asks about an invocation.
/// </summary>
internal static class Operations
{
    /// <summary>What an instance or extension call is made on.</summary>
    public static IOperation? Receiver(IInvocationOperation invocation) =>
        invocation.Instance ??
        (invocation.TargetMethod.IsExtensionMethod ? invocation.Arguments.FirstOrDefault()?.Value : null);

    /// <summary>The operation under any implicit or explicit conversions.</summary>
    public static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>
    /// The called method's name — <c>WebApplication.CreateSlimBuilder</c>, a bare name under
    /// <c>using static</c>, <c>ConfigureWebHostDefaults</c> on a chain — rather than the whole call,
    /// which may span a lambda.
    /// </summary>
    public static Location NameLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax syntax
            ? syntax.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.GetLocation(),
                SimpleNameSyntax name => name.GetLocation(),
                _ => syntax.GetLocation(),
            }
            : invocation.Syntax.GetLocation();

    /// <summary>
    /// <c>AddSingleton&lt;IStartupFilter&gt;(this)</c>, <c>AddSingleton&lt;IStartupFilter&gt;(new Module())</c>
    /// or <c>AddSingleton&lt;IStartupFilter, Module&gt;()</c>: the module putting itself in the list of
    /// startup filters, where <c>ModuleBase</c> has put it already.
    /// </summary>
    public static bool RegistersAsStartupFilter(IInvocationOperation invocation, INamedTypeSymbol? startupFilter, INamedTypeSymbol module) =>
        startupFilter is not null &&
        invocation.TargetMethod.Name.StartsWith("Add", StringComparison.Ordinal) &&
        invocation.TargetMethod.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, startupFilter)) &&
        (invocation.TargetMethod.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, module)) ||
         invocation.Arguments.Any(argument => Unwrap(argument.Value) switch
         {
             IInstanceReferenceOperation => true,
             IObjectCreationOperation creation => SymbolEqualityComparer.Default.Equals(creation.Type, module),
             _ => false,
         }));
}
