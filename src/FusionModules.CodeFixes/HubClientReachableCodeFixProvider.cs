using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// Makes a hub's client interface reachable from SignalR's generated proxy (MOD0009).
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
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit)
        {
            return document;
        }

        // Fully qualified: an assembly-level attribute sits above any file-scoped namespace, where
        // a bare name would not bind.
        var attribute = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName(InternalsVisibleToAttributeName),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal(ModuleFacts.TypedClientBuilderAssemblyName))))))))
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .NormalizeWhitespace(elasticTrivia: true)
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(LineBreakOf(root)));

        var updated = unit.AttributeLists.Any()
            ? unit.AddAttributeLists(attribute)
            : unit.WithAttributeLists(SyntaxFactory.SingletonList(attribute));

        return document.WithSyntaxRoot(updated);
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

        // The whole chain, not just the reported type. MOD0009 reports a public interface nested
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

        return document.WithSyntaxRoot(
            root.ReplaceNodes(chain, (original, _) => MakePublic(original)));
    }

    private static BaseTypeDeclarationSyntax MakePublic(BaseTypeDeclarationSyntax declaration)
    {
        var @public = SyntaxFactory.Token(SyntaxKind.PublicKeyword);

        var existing = declaration.Modifiers.FirstOrDefault(modifier =>
            modifier.IsKind(SyntaxKind.InternalKeyword) ||
            modifier.IsKind(SyntaxKind.PrivateKeyword) ||
            modifier.IsKind(SyntaxKind.ProtectedKeyword));

        if (existing != default)
        {
            // Replace rather than remove-and-add, so the doc comment above stays where it was.
            // `protected internal` and `private protected` are two tokens; the first goes, and
            // dropping the rest is what makes the result mean what the fix says it does.
            var modifiers = declaration.Modifiers.Replace(
                existing,
                @public.WithLeadingTrivia(existing.LeadingTrivia).WithTrailingTrivia(existing.TrailingTrivia));

            foreach (var redundant in modifiers
                .Where(modifier => modifier != existing &&
                                   (modifier.IsKind(SyntaxKind.InternalKeyword) ||
                                    modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                                    modifier.IsKind(SyntaxKind.ProtectedKeyword)))
                .ToArray())
            {
                modifiers = modifiers.Remove(redundant);
            }

            return declaration.WithModifiers(modifiers);
        }

        // No accessibility at all — the default. The leading trivia belongs to whichever token
        // currently comes first, and has to move to the keyword being put in front of it.
        var first = declaration.Modifiers.Count > 0
            ? declaration.Modifiers[0]
            : declaration.ChildTokens().First();

        var leading = first.LeadingTrivia;
        var stripped = first.WithLeadingTrivia(SyntaxFactory.TriviaList());

        var updated = declaration.Modifiers.Count > 0
            ? declaration.WithModifiers(declaration.Modifiers.Replace(first, stripped))
            : declaration.ReplaceToken(first, stripped);

        return updated.WithModifiers(updated.Modifiers.Insert(
            0,
            @public.WithLeadingTrivia(leading).WithTrailingTrivia(SyntaxFactory.Space)));
    }

    private static string LineBreakOf(SyntaxNode root)
    {
        var text = root.GetText();

        return text.Lines.Count > 1
            ? text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak))
            : Environment.NewLine;
    }
}
