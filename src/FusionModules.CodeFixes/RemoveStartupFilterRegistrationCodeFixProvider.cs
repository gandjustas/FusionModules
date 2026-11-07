using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// Removes a module's registration of itself as an <c>IStartupFilter</c> (MOD0009): <c>ModuleBase</c>
/// makes it already. A statement goes whole; a call in a chain leaves the rest of the chain on its
/// receiver.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveStartupFilterRegistrationCodeFixProvider)), Shared]
public sealed class RemoveStartupFilterRegistrationCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.RedundantStartupFilterId];

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
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not InvocationExpressionSyntax invocation ||
                Edit(invocation) is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove the registration: ModuleBase makes it",
                    cancellationToken => FixAsync(context.Document, invocation, cancellationToken),
                    equivalenceKey: nameof(RemoveStartupFilterRegistrationCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> FixAsync(Document document, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null || Edit(invocation) is not { } edit
            ? document
            : document.WithSyntaxRoot(edit(root));
    }

    private static System.Func<SyntaxNode, SyntaxNode>? Edit(InvocationExpressionSyntax invocation) => invocation.Parent switch
    {
        // A statement in a block goes; the body of an if or a loop cannot, and becomes {}.
        ExpressionStatementSyntax { Parent: BlockSyntax or SwitchSectionSyntax } statement =>
            root => root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!,
        ExpressionStatementSyntax statement =>
            root => root.ReplaceNode(statement, Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseStatement("{ }").WithTriviaFrom(statement)),
        _ when invocation.Expression is MemberAccessExpressionSyntax call =>
            root => root.ReplaceNode(invocation, call.Expression.WithTriviaFrom(invocation)),
        _ => null,
    };
}
