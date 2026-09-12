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
using Modulith.Analyzers;

namespace Modulith.CodeFixes;

/// <summary>
/// Adds the missing <c>[assembly: HostingStartup(typeof(TModule))]</c> (MOD0005).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RegisterModuleCodeFixProvider)), Shared]
public sealed class RegisterModuleCodeFixProvider : CodeFixProvider
{
    private const string HostingStartupAttributeName = "Microsoft.AspNetCore.Hosting.HostingStartup";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.ModuleMustBeRegisteredId];

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
            if (root.FindNode(diagnostic.Location.SourceSpan) is not TypeDeclarationSyntax declaration)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Register '{declaration.Identifier.Text}' as a module",
                    cancellationToken => RegisterAsync(context.Document, declaration, cancellationToken),
                    equivalenceKey: nameof(RegisterModuleCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> RegisterAsync(
        Document document,
        TypeDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        if (root is not CompilationUnitSyntax unit || model?.GetDeclaredSymbol(declaration, cancellationToken) is not { } type)
        {
            return document;
        }

        // The fully qualified name, because an assembly-level attribute sits above any file-scoped
        // namespace and a bare name would not bind there.
        var attribute = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName(HostingStartupAttributeName),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(
                                    SyntaxFactory.TypeOfExpression(
                                        SyntaxFactory.ParseTypeName(type.ToDisplayString()))))))))
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .NormalizeWhitespace(elasticTrivia: true)
            // Set after normalisation, which discards trivia applied before it. One line feed:
            // whatever separated the usings from the first declaration is still there below.
            .WithTrailingTrivia(SyntaxFactory.LineFeed);

        // After the usings, before everything else, which is where a reader expects it.
        var updated = unit.AttributeLists.Any()
            ? unit.AddAttributeLists(attribute)
            : unit.WithAttributeLists(SyntaxFactory.SingletonList(attribute));

        return document.WithSyntaxRoot(updated);
    }
}
