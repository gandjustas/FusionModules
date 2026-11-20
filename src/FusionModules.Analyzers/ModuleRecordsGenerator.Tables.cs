using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static FusionModules.Analyzers.ModuleRecords;

namespace FusionModules.Analyzers;

/// <content>
/// The tables a module's code maps, for MOD0017 in the host's build.
/// </content>
/// <remarks>
/// <para>
/// A module owns a table when it names it: <c>ToTable</c> on an entity's builder, in an
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> or on <c>modelBuilder.Entity&lt;T&gt;()</c> — every overload
/// that takes a name — and <c>[Table]</c> on an entity class the module declares. Only constants
/// count, which is what <c>GetConstantValue</c> answers for: a literal, a <c>const</c>, <c>nameof</c>.
/// A name read from configuration is not guessed at; the startup check sees it.
/// </para>
/// <para>
/// A module that configures another module's entity without naming its table — a relationship, an
/// index — does not own it. Neither does <c>ToView</c>: a view is how a module reads data it does
/// not own, which is the fix this rule suggests, not the leak it is after.
/// </para>
/// </remarks>
public sealed partial class ModuleRecordsGenerator
{
    private const string TableAttributeMetadataName = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";

    /// <summary>The tables the project's code maps, sorted and de-duplicated, so that reordering or repeating a call changes nothing.</summary>
    private static IncrementalValueProvider<EquatableArray<OwnedTable>> TablesOf(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsTableCandidate(node),
                static (syntax, token) => ReadToTable(syntax, token))
            .WithTrackingName(TrackingNames.TableCandidates)
            .Where(static table => table is not null)
            .Select(static (table, _) => table!.Value)
            .Collect();

        var attributes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TableAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntax, _) => ReadTableAttribute(syntax))
            .WithTrackingName(TrackingNames.TableAttributes)
            .Where(static table => table is not null)
            .Select(static (table, _) => table!.Value)
            .Collect();

        return calls
            .Combine(attributes)
            .Select(static (found, _) => new EquatableArray<OwnedTable>(found.Left
                .Concat(found.Right)
                .Distinct()
                .OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Table, StringComparer.Ordinal)
                .ThenBy(table => table.EntityIdentity, StringComparer.Ordinal)))
            .WithTrackingName(TrackingNames.OwnedTables);
    }

    private static bool IsTableCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax invocation &&
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        } is "ToTable" or "SplitToTable";

    /// <summary>
    /// The table a call to EF Core's relational <c>ToTable</c> or <c>SplitToTable</c> names, with the
    /// entity when the builder is an entity's own and the call is <c>ToTable</c>.
    /// </summary>
    private static OwnedTable? ReadToTable(GeneratorSyntaxContext context, CancellationToken token)
    {
        if (context.SemanticModel.GetOperation(context.Node, token) is not IInvocationOperation invocation)
        {
            return null;
        }

        // The relational extensions: RelationalEntityTypeBuilderExtensions and its kin for owned types.
        var method = invocation.TargetMethod;
        if (!method.IsStatic ||
            method.Parameters.Length < 2 ||
            method.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore" ||
            method.Parameters[0].Type is not INamedTypeSymbol builder ||
            builder.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore.Metadata.Builders")
        {
            return null;
        }

        // The name is the first string after the builder, the schema the second, whichever order the
        // call wrote them in; an overload with no name (ToTable(table => …)) names no table.
        var names = invocation.Arguments
            .Where(argument => argument.Parameter is { Ordinal: > 0, Type.SpecialType: SpecialType.System_String })
            .OrderBy(argument => argument.Parameter!.Ordinal)
            .Select(argument => argument.Value.ConstantValue)
            .ToList();

        if (names.Count == 0 || !names[0].HasValue || names[0].Value is not string { Length: > 0 } table)
        {
            return null;
        }

        string schema;
        if (names.Count < 2)
        {
            schema = string.Empty;
        }
        else if (!names[1].HasValue)
        {
            // A constant table in a schema read from configuration: which table it is, is not known.
            return null;
        }
        else
        {
            schema = names[1].Value as string ?? string.Empty;
        }

        ITypeSymbol? entity = method.Name == "ToTable" && builder is { Name: "EntityTypeBuilder", TypeArguments.Length: 1 }
            ? builder.TypeArguments[0]
            : null;

        if (entity is not null && (entity.TypeKind == TypeKind.Error || ContainsTypeParameter(entity)))
        {
            return null;
        }

        // A shared-type entity — a many-to-many join table, UsingEntity<Dictionary<string, object>> —
        // is a framework type that every such table shares: the table is the module's, the type is
        // nobody's entity.
        if (entity is not null && TopologyConventions.IsFrameworkAssembly(entity.ContainingAssembly?.Name))
        {
            entity = null;
        }

        // A builder of a type parameter's entity in a generic helper maps a different entity at every
        // call, and its tables are named by the caller's arguments; nothing is recorded.
        if (builder.TypeArguments.Any(ContainsTypeParameter))
        {
            return null;
        }

        return Owned(schema, table, entity);
    }

    /// <summary>The table <c>[Table]</c> on one of the module's own classes names.</summary>
    private static OwnedTable? ReadTableAttribute(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol { IsGenericType: false } type ||
            context.Attributes.FirstOrDefault() is not { ConstructorArguments.Length: 1 } attribute ||
            attribute.ConstructorArguments[0].Value is not string { Length: > 0 } table)
        {
            return null;
        }

        var schema = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Schema").Value.Value as string;
        return Owned(schema ?? string.Empty, table, type);
    }

    private static OwnedTable Owned(string schema, string table, ITypeSymbol? entity) =>
        entity is null
            ? new OwnedTable(schema, table, string.Empty, string.Empty, string.Empty)
            : new OwnedTable(
                schema,
                table,
                TypeNames.FullName(entity),
                TypeNames.Identity(entity),
                string.Join(SpellingSeparator.ToString(), TypeNames.TypeSpellings(entity)));
}
