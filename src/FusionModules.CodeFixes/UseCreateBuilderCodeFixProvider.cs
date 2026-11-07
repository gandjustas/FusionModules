using System.Collections.Immutable;
using System.Composition;
using FusionModules.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionModules.CodeFixes;

/// <summary>
/// Switches a slim or empty builder to the one that loads modules (MOD0006):
/// <c>WebApplication.CreateSlimBuilder(args)</c> becomes <c>WebApplication.CreateBuilder(args)</c>.
/// </summary>
/// <remarks>
/// The name alone changes. <c>CreateBuilder</c> has every overload the other two have — none, the
/// arguments, <c>WebApplicationOptions</c> — so the arguments stay as they are.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseCreateBuilderCodeFixProvider)), Shared]
public sealed class UseCreateBuilderCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.BuilderDoesNotLoadModulesId];

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
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not SimpleNameSyntax name)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Use {ModuleFacts.CreateBuilderMethodName}",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceToken(
                        name.Identifier,
                        SyntaxFactory.Identifier(ModuleFacts.CreateBuilderMethodName).WithTriviaFrom(name.Identifier)))),
                    equivalenceKey: nameof(UseCreateBuilderCodeFixProvider)),
                diagnostic);
        }
    }
}
