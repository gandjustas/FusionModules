using System.Collections.Generic;
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
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// Moves an override of the obsolete <c>ConfigureServices(WebHostBuilderContext, IServiceCollection)</c>
/// into <c>ConfigureServices(IHostApplicationBuilder)</c> (CS0672).
/// </summary>
/// <remarks>
/// <para>
/// The body moves as written, with three renames: the service collection becomes
/// <c>builder.Services</c>, <c>context.Configuration</c> becomes <c>builder.Configuration</c>, and
/// <c>context.HostingEnvironment</c> becomes <c>builder.Environment</c> — cast to
/// <c>IWebHostEnvironment</c> where the code needs the web environment's own members. When the module
/// overrides the builder overload already, the body goes after its statements, which is the order the
/// package ran the two in.
/// </para>
/// <para>
/// Any other use of the context — passing it whole to a helper, say — has no counterpart on the
/// builder, and the fix is not offered; neither is it when the body already uses the name the builder
/// would take.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseBuilderOverloadCodeFixProvider)), Shared]
public sealed class UseBuilderOverloadCodeFixProvider : CodeFixProvider
{
    private const string MemberOverridesObsoleteMember = "CS0672";
    private const string HostingNamespace = "Microsoft.Extensions.Hosting";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [MemberOverridesObsoleteMember];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } method ||
                !IsObsoleteModuleOverride(method, model, context.CancellationToken) ||
                Rewrite(method, model, context.CancellationToken) is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Move into ConfigureServices(IHostApplicationBuilder)",
                    cancellationToken => FixAsync(context.Document, method, cancellationToken),
                    equivalenceKey: nameof(UseBuilderOverloadCodeFixProvider)),
                diagnostic);
        }
    }

    private static bool IsObsoleteModuleOverride(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken) =>
        method.Identifier.Text == "ConfigureServices" &&
        method.ParameterList.Parameters.Count == 2 &&
        model.GetDeclaredSymbol(method, cancellationToken)?.OverriddenMethod?.ContainingType is { } declaring &&
        SymbolEqualityComparer.Default.Equals(declaring, ModuleFacts.GetModuleBaseType(model.Compilation));

    private static async Task<Document> FixAsync(Document document, MethodDeclarationSyntax obsolete, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit || model is null ||
            obsolete.Parent is not ClassDeclarationSyntax declaration ||
            Rewrite(obsolete, model, cancellationToken) is not { } rewrite)
        {
            return document;
        }

        var lineBreak = ModuleEditing.LineBreakOf(root);
        var existing = ModuleEditing.FindOverride(declaration, model, "ConfigureServices", FusionModules.Analyzers.ModuleFacts.HostApplicationBuilderMetadataName, cancellationToken);

        ClassDeclarationSyntax updated;
        if (existing is not null)
        {
            var marked = declaration.ReplaceNodes(
                [obsolete, existing],
                (original, _) => original.WithAdditionalAnnotations(original == obsolete ? Marker : Target));
            var target = marked.GetAnnotatedNodes(Target).OfType<MethodDeclarationSyntax>().Single();
            var appended = ModuleEditing.AppendToOverride(marked, target, rewrite.Body, lineBreak);
            updated = appended.RemoveNode(appended.GetAnnotatedNodes(Marker).Single(), SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // In place: the header up to the parameter list as it was, a new parameter list, the body.
            var full = obsolete.ToFullString();
            var header = full.Substring(0, obsolete.ParameterList.SpanStart - obsolete.FullSpan.Start);
            var memberIndent = ModuleEditing.IndentationOf(obsolete);
            var text = header + $"(IHostApplicationBuilder {rewrite.Parameter})" + lineBreak +
                memberIndent + "{" + lineBreak +
                ModuleEditing.Indent(rewrite.Body, memberIndent + "    ", lineBreak) + lineBreak +
                memberIndent + "}" + obsolete.GetTrailingTrivia().ToFullString();
            updated = declaration.ReplaceNode(obsolete, SyntaxFactory.ParseMemberDeclaration(text)!);
        }

        var newUnit = ModuleEditing.EnsureUsings(unit.ReplaceNode(declaration, updated), model, declaration.SpanStart, [HostingNamespace]);
        return document.WithSyntaxRoot(newUnit);
    }

    private static readonly SyntaxAnnotation Marker = new(nameof(UseBuilderOverloadCodeFixProvider));

    private static readonly SyntaxAnnotation Target = new(nameof(UseBuilderOverloadCodeFixProvider) + ".Target");

    /// <summary>The body rewritten for the builder, with the builder's name; null when it cannot be.</summary>
    private static Rewritten? Rewrite(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken)
    {
        if (method.Parent is not ClassDeclarationSyntax declaration ||
            model.GetDeclaredSymbol(method, cancellationToken) is not { } symbol)
        {
            return null;
        }

        var context = symbol.Parameters[0];
        var services = symbol.Parameters[1];
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression;
        if (body is null)
        {
            return null;
        }

        var existing = ModuleEditing.FindOverride(declaration, model, "ConfigureServices", FusionModules.Analyzers.ModuleFacts.HostApplicationBuilderMetadataName, cancellationToken);
        var existingBody = (SyntaxNode?)existing?.Body ?? existing?.ExpressionBody?.Expression;

        // With an override there already, its parameter's name is the builder's, and nothing in the
        // moved body may mean something else by it. Without one, any free name will do.
        var builder = existing is not null
            ? existing.ParameterList.Parameters[0].Identifier.Text
            : BuilderRewriting.PickName([body]);
        if (builder is null ||
            (existing is not null && body.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.Text == builder)) ||
            BuilderRewriting.Rewrite(body, context, services, builder, model, module: null, cancellationToken) is not { } text)
        {
            return null;
        }

        if (existingBody is not null)
        {
            // Appended after the override's statements: a return there would skip what used to run
            // regardless, and a local of the same name would not compile — not even in a block of
            // its own, since C# keeps a name for the whole of the enclosing scope.
            if (BuilderRewriting.Returns(existingBody) ||
                BuilderRewriting.DeclaredNames(existingBody).Overlaps(BuilderRewriting.DeclaredNames(body)))
            {
                return null;
            }
        }

        return new Rewritten(builder, text);
    }

    private sealed class Rewritten(string parameter, string body)
    {
        public string Parameter { get; } = parameter;

        public string Body { get; } = body;
    }
}
