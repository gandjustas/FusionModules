using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// Makes a public type in a module internal (MOD0001).
/// </summary>
/// <remarks>
/// The fix that matters most during a migration. A service converted into a module has every one
/// of its types public, and Fix All turns that into one operation rather than a hundred.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakeTypeInternalCodeFixProvider)), Shared]
public sealed class MakeTypeInternalCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.ModuleMustNotExposePublicTypesId];

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
            if (root.FindNode(diagnostic.Location.SourceSpan) is not MemberDeclarationSyntax declaration ||
                declaration is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Make internal",
                    cancellationToken => MakeInternalAsync(context.Document, declaration, cancellationToken),
                    equivalenceKey: nameof(MakeTypeInternalCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> MakeInternalAsync(
        Document document,
        MemberDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        // Through the generator rather than by swapping tokens: it keeps leading trivia — the doc
        // comment and the blank line above it — attached where it was.
        var updated = SyntaxGenerator.GetGenerator(document).WithAccessibility(declaration, Accessibility.Internal);

        return document.WithSyntaxRoot(root.ReplaceNode(declaration, updated));
    }
}
