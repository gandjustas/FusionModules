using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// Makes a hub's client interface reachable from SignalR's generated proxy (MOD0010).
/// </summary>
/// <remarks>
/// Two ways out, and the order matters. Granting the proxy's assembly access to internals is one
/// line, ends there, and leaves the module's surface exactly as MOD0001 wants it. Making the
/// interface public is the other, and it is deliberately incomplete: the compiler then walks you
/// through the interface's signatures with CS0050 and CS0051, and every type it names goes public
/// with it.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HubClientReachableCodeFixProvider)), Shared]
public sealed class HubClientReachableCodeFixProvider : CodeFixProvider
{
    private const string InternalsVisibleToAttributeName = "System.Runtime.CompilerServices.InternalsVisibleTo";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.HubClientTypeMustBeReachableId];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan) is not BaseTypeDeclarationSyntax declaration)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Grant SignalR's proxy access to internals",
                    cancellationToken => GrantAccessAsync(context.Document, cancellationToken),
                    equivalenceKey: nameof(GrantAccessAsync)),
                diagnostic);

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Make public",
                    cancellationToken => MakePublicAsync(context.Document, declaration, cancellationToken),
                    equivalenceKey: nameof(MakePublicAsync)),
                diagnostic);
        }
    }

    private static async Task<Document> GrantAccessAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax unit)
        {
            return document;
        }

        var generator = SyntaxGenerator.GetGenerator(document);
        var attribute = generator.Attribute(
            InternalsVisibleToAttributeName,
            generator.LiteralExpression(ModuleFacts.TypedClientBuilderAssemblyName));

        return SyntaxEditing.AddAssemblyAttribute(document, unit, attribute);
    }

    private static async Task<Document> MakePublicAsync(
        Document document,
        BaseTypeDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        // The whole chain, not just the reported type. MOD0010 reports a public interface nested
        // in an internal one, because the proxy outside the assembly cannot see it either way —
        // and there the type to change is the container.
        var chain = declaration
            .AncestorsAndSelf()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(type => !type.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToArray();

        if (chain.Length == 0)
        {
            return document;
        }

        // The generator knows every spelling of accessibility — none at all, `protected internal`,
        // `private protected` — and keeps the doc comment where it was. The rewritten node, not
        // the original: a container's rewrite has to carry the nested type's along with it.
        var generator = SyntaxGenerator.GetGenerator(document);

        return document.WithSyntaxRoot(
            root.ReplaceNodes(chain, (_, rewritten) => generator.WithAccessibility(rewritten, Accessibility.Public)));
    }
}
