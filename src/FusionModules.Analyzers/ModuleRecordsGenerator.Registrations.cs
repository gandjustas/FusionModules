using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static FusionModules.Analyzers.ModuleRecords;

namespace FusionModules.Analyzers;

/// <content>
/// The services a module's code registers, for MOD0014 in the host's build.
/// </content>
/// <remarks>
/// <para>
/// Strings rather than <c>typeof</c>: the implementation is often a private nested type, which an
/// assembly attribute cannot name, and the host only ever compares names.
/// </para>
/// <para>
/// Only what the module's own code says is recorded. Registrations made inside a library's
/// extension method are invisible here; the runtime check sees those, at startup.
/// </para>
/// </remarks>
public sealed partial class ModuleRecordsGenerator
{
    private static bool IsRegistrationCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

        return name is not null && IsRecorded(name);
    }

    /// <summary>Whether a call by this name is one the generator records.</summary>
    /// <remarks>
    /// Keyed registrations are left out — two keys are two services — and so is
    /// <c>TryAddEnumerable</c>, which says in its name that the service is a sequence.
    /// <c>TryAdd</c> is in: across modules it only turns "last wins" into "first wins", and the
    /// registration it drops is one the runtime check never sees. The lifetime does not matter: a
    /// scoped or transient service is resolved from the last registration just as a singleton is.
    /// <para>
    /// A typed <c>HttpClient</c> is in too: <c>AddHttpClient&lt;ICalculator, Calculator&gt;()</c> registers
    /// <c>ICalculator</c> as surely as <c>AddTransient</c> does, and it is the usual way a remote
    /// implementation of a contract is written.
    /// </para>
    /// </remarks>
    private static bool IsRecorded(string method) =>
        method is "AddSingleton" or "TryAddSingleton" or "AddScoped" or "TryAddScoped" or "AddTransient" or "TryAddTransient"
            or "AddHttpClient";

    private static Registration? ReadRegistration(GeneratorSyntaxContext context, CancellationToken token)
    {
        if (context.SemanticModel.GetOperation(context.Node, token) is not IInvocationOperation invocation)
        {
            return null;
        }

        var method = invocation.TargetMethod;
        if (!method.IsStatic ||
            method.Parameters.Length == 0 ||
            method.Parameters[0].Type.Name != "IServiceCollection" ||
            method.ContainingNamespace?.ToDisplayString() is not ("Microsoft.Extensions.DependencyInjection" or "Microsoft.Extensions.DependencyInjection.Extensions"))
        {
            return null;
        }

        // The arguments after the collection, in parameter order whichever order the call wrote them in.
        var arguments = invocation.Arguments
            .Where(argument => argument.Parameter is { Ordinal: > 0 })
            .OrderBy(argument => argument.Parameter!.Ordinal)
            .Select(argument => argument.Value)
            .ToList();

        // A typed client registers TClient, implemented by TImplementation or by itself. Its arguments
        // configure the HttpClient (a name, a configure delegate), not the implementation; without type
        // arguments it is a named client and registers no service at all.
        if (method.Name == "AddHttpClient")
        {
            return method.TypeArguments.Length == 0
                ? null
                : Record(method.TypeArguments[0], client => Named(method.TypeArguments.Length > 1 ? method.TypeArguments[1] : client));
        }

        ITypeSymbol? service;
        int rest;
        if (method.TypeArguments.Length > 0)
        {
            service = method.TypeArguments[0];
            rest = 0;
        }
        else if (arguments.Count > 0 && arguments[0] is ITypeOfOperation serviceOf)
        {
            service = serviceOf.TypeOperand;
            rest = 1;
        }
        else
        {
            return null;
        }

        // <TService, TImplementation>(factory) is still a factory: the runtime sees a delegate.
        return Record(service, recorded => arguments.Count > rest
            ? Implementation(arguments[rest], recorded, context.SemanticModel.Compilation.AssemblyName)
            : Named(method.TypeArguments.Length > 1 ? method.TypeArguments[1] : recorded));
    }

    /// <summary>
    /// The registration of <paramref name="service"/>, or null for a service that is not recorded:
    /// one that does not bind, one that names a type parameter, or one shared by design.
    /// </summary>
    private static Registration? Record(ITypeSymbol? service, Func<ITypeSymbol, (string Display, string Identity)> implementationOf)
    {
        service = Definition(service);
        if (service is null || service.TypeKind == TypeKind.Error || ContainsTypeParameter(service) || TypeNames.IsSharedByDesign(service))
        {
            return null;
        }

        var implementation = implementationOf(service);
        return new Registration(
            TypeNames.FullName(service),
            TypeNames.Identity(service),
            implementation.Display,
            implementation.Identity,
            string.Join(SpellingSeparator.ToString(), TypeNames.TypeSpellings(service)));
    }

    /// <summary>What a registration's last argument says the implementation is.</summary>
    /// <remarks>
    /// Judged as the runtime check judges a descriptor, so that the two agree: a type is itself, an
    /// instance is its type, and a factory is its own implementation — two modules' factories differ
    /// even when they build the same type, since each builds it its own way. What the factory
    /// returns is named only to help the reader.
    /// </remarks>
    private static (string Display, string Identity) Implementation(IOperation argument, ITypeSymbol service, string? assemblyName)
    {
        var value = Unwrap(argument);
        switch (value)
        {
            case ITypeOfOperation typeOf:
                return Named(typeOf.TypeOperand);

            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }:
                return Factory(assemblyName, Returned(lambda.Body, service));

            case IAnonymousFunctionOperation lambda:
                return Factory(assemblyName, Returned(lambda.Body, service));

            case IDelegateCreationOperation { Target: IMethodReferenceOperation reference }:
                return Factory(assemblyName, IsInformative(reference.Method.ReturnType, service) ? reference.Method.ReturnType : null);

            default:
                // An instance: the runtime compares its type. The compiler knows only the static
                // type, which is no answer when it is an interface or an abstract class.
                return value.Type is INamedTypeSymbol { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct } type &&
                    type.SpecialType != SpecialType.System_Object
                    ? Named(type)
                    : Unnamed($"(instance in {assemblyName})");
        }
    }

    /// <summary>The one type a factory's body returns, when it says more than the service type does.</summary>
    private static ITypeSymbol? Returned(IBlockOperation body, ITypeSymbol service)
    {
        var returned = Returns(body).Select(operation => Unwrap(operation).Type).ToList();
        if (returned.Count == 0 || returned[0] is not { } first)
        {
            return null;
        }

        return returned.All(type => SymbolEqualityComparer.Default.Equals(type, first)) && IsInformative(first, service) ? first : null;
    }

    /// <summary>The values a body returns, not counting those of the lambdas and local functions inside it.</summary>
    private static IEnumerable<IOperation> Returns(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            if (child is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                continue;
            }

            if (child is IReturnOperation { ReturnedValue: { } value })
            {
                yield return value;
                continue;
            }

            foreach (var nested in Returns(child))
            {
                yield return nested;
            }
        }
    }

    private static bool IsInformative(ITypeSymbol type, ITypeSymbol service) =>
        type.TypeKind is not (TypeKind.Error or TypeKind.TypeParameter or TypeKind.Dynamic) &&
        type.SpecialType != SpecialType.System_Object &&
        !SymbolEqualityComparer.Default.Equals(type, service);

    private static (string Display, string Identity) Factory(string? assemblyName, ITypeSymbol? returned) =>
        Unnamed(returned is null ? $"(factory in {assemblyName})" : $"(factory in {assemblyName} returning {TypeNames.FullName(Definition(returned)!)})");

    /// <summary>A type, by the name a reader knows it and by the name that tells it from a namesake in another assembly.</summary>
    private static (string Display, string Identity) Named(ITypeSymbol type)
    {
        var definition = Definition(type)!;
        return (TypeNames.FullName(definition), TypeNames.Identity(definition));
    }

    /// <summary>An implementation with no type to name; the text names its assembly, so it identifies itself.</summary>
    private static (string Display, string Identity) Unnamed(string text) => (text, text);

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }

    /// <summary><c>typeof(IRepository&lt;&gt;)</c> names the definition; the definition's spellings are what an entry matches.</summary>
    private static ITypeSymbol? Definition(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsUnboundGenericType: true } unbound ? unbound.OriginalDefinition : type;

    /// <summary>
    /// A registration inside a generic helper names a type parameter, which is a different service
    /// at every call. A generic definition — <c>typeof(IRepository&lt;&gt;)</c> — is one service.
    /// </summary>
    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => !named.IsDefinition && named.TypeArguments.Any(ContainsTypeParameter),
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        _ => false,
    };
}
