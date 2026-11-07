using System.Collections.Immutable;
using System.Composition;
using FusionModules.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace FusionModules.CodeFixes;

/// <summary>
/// Validates the builder where the modules are loaded (MOD0012): <c>CreateBuilder(args)</c> becomes
/// <c>CreateBuilder(args).ValidateModules()</c>, and <c>ConfigureWebHostDefaults(…)</c> on a generic
/// host becomes <c>ConfigureWebHostDefaults(…).ValidateModules()</c>.
/// </summary>
/// <remarks>
/// At the creating call rather than on a later line: the hosting startups have run by the time
/// the builder exists, so that is the earliest the check can say anything, and a check written
/// into the same expression cannot be separated from it by a later edit.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ValidateModulesCodeFixProvider)), Shared]
public sealed class ValidateModulesCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.HostDoesNotValidateModulesId];

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
            // The diagnostic covers CreateBuilder's whole call, or only ConfigureWebHostDefaults'
            // name: either way the call to extend is the nearest one around it.
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                    .AncestorsAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault() is not { } creation)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Call {ModuleFacts.ValidateModulesMethodName}()",
                    cancellationToken => ValidateAsync(context.Document, creation, cancellationToken),
                    equivalenceKey: nameof(ValidateModulesCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> ValidateAsync(
        Document document,
        InvocationExpressionSyntax creation,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit || model is null)
        {
            return document;
        }

        var generator = SyntaxGenerator.GetGenerator(document);
        var validated = generator
            .InvocationExpression(generator.MemberAccessExpression(creation.WithoutTrivia(), ModuleFacts.ValidateModulesMethodName))
            .WithTriviaFrom(creation);

        // The extension lives in the builder's own namespace, which the Web SDK imports
        // implicitly. A host that turned implicit usings off gets the using it needs.
        var updated = SyntaxEditing.AddUsingIfMissing(
            unit.ReplaceNode(creation, validated),
            model,
            creation.SpanStart,
            ModuleFacts.ValidateModulesNamespace);

        return document.WithSyntaxRoot(updated);
    }
}
