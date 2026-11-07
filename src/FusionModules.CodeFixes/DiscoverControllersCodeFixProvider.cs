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
/// Writes the line MOD0011 is about: the module's own assembly as an MVC application part, and
/// <c>AllowInternalControllers()</c> when its controllers are internal.
/// </summary>
/// <remarks>
/// The line goes into the module class, which is usually not the file the diagnostic is in — the
/// diagnostic sits on a controller. An existing <c>AddControllers()</c> statement in the module's
/// <c>ConfigureServices</c> is extended rather than repeated.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DiscoverControllersCodeFixProvider)), Shared]
public sealed class DiscoverControllersCodeFixProvider : CodeFixProvider
{
    private const string DependencyInjectionNamespace = "Microsoft.Extensions.DependencyInjection";
    private const string FusionModulesNamespace = "FusionModules";
    private const string HostingNamespace = "Microsoft.Extensions.Hosting";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.ControllersNotDiscoveredId];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            var missing = diagnostic.Properties.TryGetValue(ControllerDiscoveryAnalyzer.MissingProperty, out var value) ? value : null;
            var mvc = diagnostic.Properties.TryGetValue(ControllerDiscoveryAnalyzer.MvcProperty, out var kind) ? kind : null;
            var allowInternal = diagnostic.Properties.ContainsKey(ControllerDiscoveryAnalyzer.AllowInternalProperty);

            // Offered only where it can write the call: a fix that changes nothing is worse than none.
            if (missing == ControllerDiscoveryAnalyzer.MissingInternalControllers &&
                await FindAllowTargetAsync(context.Document.Project, context.CancellationToken).ConfigureAwait(false) is null)
            {
                continue;
            }

            var title = missing == ControllerDiscoveryAnalyzer.MissingInternalControllers
                ? "Call AllowInternalControllers() in the module"
                : "Add the module's assembly as an MVC application part";

            context.RegisterCodeFix(
                CodeAction.Create(
                    title,
                    cancellationToken => FixAsync(context.Document.Project, missing, mvc, allowInternal, cancellationToken),
                    equivalenceKey: nameof(DiscoverControllersCodeFixProvider) + missing),
                diagnostic);
        }
    }

    private static async Task<Solution> FixAsync(Project project, string? missing, string? mvc, bool allowInternal, CancellationToken cancellationToken)
    {
        var solution = project.Solution;
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return solution;
        }

        if (missing == ControllerDiscoveryAnalyzer.MissingInternalControllers)
        {
            return await AllowInternalAsync(project, compilation, cancellationToken).ConfigureAwait(false);
        }

        if (await ModuleEditing.FindAsync(project, cancellationToken).ConfigureAwait(false) is not { } module)
        {
            return solution;
        }

        var part = $".AddApplicationPart(typeof({module.Declaration.Identifier.Text}).Assembly)" + (allowInternal ? ".AllowInternalControllers()" : string.Empty);

        var model = await module.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await module.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is not CompilationUnitSyntax unit)
        {
            return solution;
        }

        var configureServices = ModuleEditing.FindOverride(module.Declaration, model, "ConfigureServices", FusionModules.Analyzers.ModuleFacts.HostApplicationBuilderMetadataName, cancellationToken);
        var mvcBuilder = compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.MvcBuilderMetadataName);

        ClassDeclarationSyntax updated;
        var lineBreak = ModuleEditing.LineBreakOf(root);
        if (FindMvcStatement(configureServices, model, mvcBuilder, cancellationToken) is { } existing)
        {
            // AddControllers() is already there: the part goes on the end of that call.
            var extended = SyntaxFactory.ParseExpression(existing.WithoutTrivia().ToString() + part)
                .WithTriviaFrom(existing);
            updated = module.Declaration.ReplaceNode(existing, extended);
        }
        else if (configureServices is not null)
        {
            var parameter = configureServices.ParameterList.Parameters[0].Identifier.Text;
            updated = ModuleEditing.AppendToOverride(
                module.Declaration, configureServices, $"{parameter}.Services.{AddMvc(mvc)}(){part};", lineBreak);
        }
        else
        {
            updated = ModuleEditing.AddOverride(
                module.Declaration,
                "protected override void ConfigureServices(IHostApplicationBuilder builder)",
                $"builder.Services.{AddMvc(mvc)}(){part};",
                lineBreak);
        }

        var namespaces = new List<string> { DependencyInjectionNamespace };
        if (allowInternal)
        {
            namespaces.Add(FusionModulesNamespace);
        }

        if (configureServices is null)
        {
            namespaces.Add(HostingNamespace);
        }

        var newUnit = ModuleEditing.EnsureUsings(unit.ReplaceNode(module.Declaration, updated), model, module.Declaration.SpanStart, namespaces);
        return solution.WithDocumentSyntaxRoot(module.Document.Id, newUnit);
    }

    /// <summary>The MVC registration for what the module has: pages, views, or controllers.</summary>
    private static string AddMvc(string? mvc) => mvc switch
    {
        ControllerDiscoveryAnalyzer.RazorPages => "AddRazorPages",
        ControllerDiscoveryAnalyzer.Views => "AddControllersWithViews",
        _ => "AddControllers",
    };

    /// <summary>Appends <c>.AllowInternalControllers()</c> where <see cref="FindAllowTargetAsync"/> says.</summary>
    private static async Task<Solution> AllowInternalAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        if (await FindAllowTargetAsync(project, cancellationToken).ConfigureAwait(false) is not { } target ||
            project.GetDocument(target.Id) is not { } document ||
            await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax unit ||
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return project.Solution;
        }

        var node = unit.FindNode(target.Span, getInnermostNodeForTie: false) is ExpressionSyntax found && found.Span == target.Span
            ? found
            : unit.DescendantNodes().OfType<ExpressionSyntax>().First(e => e.Span == target.Span);
        var allowed = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, node.WithoutTrailingTrivia(), SyntaxFactory.IdentifierName("AllowInternalControllers")))
            .WithTrailingTrivia(node.GetTrailingTrivia());

        var newUnit = ModuleEditing.EnsureUsings(unit.ReplaceNode(node, allowed), model, node.SpanStart, [FusionModulesNamespace]);
        return project.Solution.WithDocumentSyntaxRoot(document.Id, newUnit);
    }

    /// <summary>
    /// Where <c>.AllowInternalControllers()</c> goes: right after the module's <c>AddApplicationPart</c>
    /// call, or — for a module that manages its parts with <c>ConfigureApplicationPartManager</c> — at the
    /// end of a statement whose whole expression is an <c>IMvcBuilder</c>. Null when there is neither,
    /// and then the fix is not offered.
    /// </summary>
    private static async Task<(DocumentId Id, Microsoft.CodeAnalysis.Text.TextSpan Span)?> FindAllowTargetAsync(Project project, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var call = root?.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(invocation => ControllerDiscoveryAnalyzer.InvocationName(invocation) == "AddApplicationPart");
            if (call is not null)
            {
                return (document.Id, call.Span);
            }
        }

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var mvcBuilder = compilation?.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.MvcBuilderMetadataName);
        if (mvcBuilder is null)
        {
            return null;
        }

        foreach (var document in project.Documents)
        {
            // This runs when the light bulb opens, so the semantic model is built only for a file that
            // makes or configures an MVC builder, and asked only about the statements that do.
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            {
                continue;
            }

            var expressions = root.DescendantNodes().OfType<ExpressionStatementSyntax>().Select(statement => statement.Expression)
                .Concat(root.DescendantNodes().OfType<ArrowExpressionClauseSyntax>().Select(arrow => arrow.Expression))
                .Where(MakesAnMvcBuilder)
                .ToList();
            if (expressions.Count == 0 ||
                await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                continue;
            }

            var builder = expressions.FirstOrDefault(expression =>
                SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(expression, cancellationToken).Type, mvcBuilder));
            if (builder is not null)
            {
                return (document.Id, builder.Span);
            }
        }

        return null;
    }

    /// <summary>The calls that make or configure an <c>IMvcBuilder</c>, by name.</summary>
    private static readonly System.Collections.Generic.HashSet<string> MvcBuilderCalls =
    [
        "AddControllers", "AddControllersWithViews", "AddRazorPages", "AddMvc", "AddMvcCore", "ConfigureApplicationPartManager",
    ];

    private static bool MakesAnMvcBuilder(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(invocation => ControllerDiscoveryAnalyzer.InvocationName(invocation) is { } name && MvcBuilderCalls.Contains(name));

    /// <summary>
    /// A statement of the override whose whole expression is an <c>IMvcBuilder</c> — the
    /// <c>AddControllers()</c> chain — which can take the application part on its end.
    /// </summary>
    private static ExpressionSyntax? FindMvcStatement(MethodDeclarationSyntax? method, SemanticModel model, INamedTypeSymbol? mvcBuilder, CancellationToken cancellationToken)
    {
        if (method is null || mvcBuilder is null)
        {
            return null;
        }

        var expressions = method.ExpressionBody is { } arrow
            ? [arrow.Expression]
            : method.Body?.Statements.OfType<ExpressionStatementSyntax>().Select(statement => statement.Expression) ?? [];

        return expressions.FirstOrDefault(expression =>
            SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(expression, cancellationToken).Type, mvcBuilder));
    }
}
