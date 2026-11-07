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
/// Turns a hand-written <c>IHostingStartup</c> into a <c>ModuleBase</c> module (MOD0002).
/// </summary>
/// <remarks>
/// <para>
/// <c>IHostingStartup</c> in the base list becomes <c>ModuleBase</c>, and <c>Configure(IWebHostBuilder)</c>
/// becomes <c>ConfigureServices(IHostApplicationBuilder)</c> when all it does is call
/// <c>builder.ConfigureServices(...)</c>: the lambdas' bodies move in, rewritten for the builder.
/// The interface has to go from the base list, not just gain a base class: re-implementing it would
/// bypass <c>ModuleBase</c>'s own implementation, which is what registers and validates the module.
/// </para>
/// <para>
/// <c>ModuleBase</c> also registers the module as an <c>IStartupFilter</c>, so a registration of the
/// module as one is dropped, and an <c>IStartupFilter</c> implementation of the usual shape —
/// <c>next(app)</c>, then the module's own middleware — becomes an override of
/// <c>Configure(IApplicationBuilder)</c>, which <c>ModuleBase</c> calls after <c>next</c>.
/// </para>
/// <para>
/// Anything else — other calls on the web host builder, a startup filter that runs before
/// <c>next</c>, a base class already — has no direct counterpart, and the fix is not offered.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseModuleBaseCodeFixProvider)), Shared]
public sealed class UseModuleBaseCodeFixProvider : CodeFixProvider
{

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.HostingStartupTypeMustBeModuleId];

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
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not TypeSyntax typeSyntax ||
                model.GetTypeInfo(typeSyntax, context.CancellationToken).Type is not INamedTypeSymbol type ||
                type.DeclaringSyntaxReferences.Length != 1 ||
                await type.DeclaringSyntaxReferences[0].GetSyntaxAsync(context.CancellationToken).ConfigureAwait(false) is not ClassDeclarationSyntax declaration ||
                context.Document.Project.GetDocument(declaration.SyntaxTree) is not { } declaringDocument)
            {
                continue;
            }

            var declaringModel = await declaringDocument.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (declaringModel is null || Plan(declaration, declaringModel, context.CancellationToken) is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Derive '{type.Name}' from ModuleBase",
                    cancellationToken => FixAsync(declaringDocument, declaration, cancellationToken),
                    equivalenceKey: nameof(UseModuleBaseCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Solution> FixAsync(Document document, ClassDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit || model is null || Plan(declaration, model, cancellationToken) is not { } plan)
        {
            return document.Project.Solution;
        }

        var lineBreak = ModuleEditing.LineBreakOf(root);
        var memberIndent = ModuleEditing.MemberIndent(declaration);
        var namespaces = new List<string> { "FusionModules" };

        // Each replaced member becomes its override in place, or goes when there is nothing to keep.
        var members = new Dictionary<SyntaxNode, MemberDeclarationSyntax?>
        {
            [plan.HostingConfigure] = plan.Services.Length == 0
                ? null
                : Member(plan.HostingConfigure, $"protected override void ConfigureServices(IHostApplicationBuilder {plan.Builder})", plan.Services, memberIndent, lineBreak),
        };
        if (plan.Services.Length > 0)
        {
            namespaces.Add("Microsoft.Extensions.Hosting");
        }

        if (plan.FilterConfigure is not null)
        {
            members[plan.FilterConfigure] = plan.Pipeline is null or { Length: 0 }
                ? null
                : Member(plan.FilterConfigure, $"protected override void Configure(IApplicationBuilder {plan.App})", plan.Pipeline, memberIndent, lineBreak);
            if (plan.Pipeline is { Length: > 0 })
            {
                namespaces.Add("Microsoft.AspNetCore.Builder");
            }
        }

        var updated = declaration.ReplaceNodes(
            members.Keys.Concat([declaration.BaseList!]),
            (original, current) => original == declaration.BaseList
                ? NewBaseList(declaration.BaseList!, plan.Dropped)
                : members[original] ?? current.WithAdditionalAnnotations(Remove));
        updated = updated.RemoveNodes(updated.GetAnnotatedNodes(Remove), SyntaxRemoveOptions.KeepNoTrivia)!;

        var newUnit = ModuleEditing.EnsureUsings(unit.ReplaceNode(declaration, updated), model, declaration.SpanStart, namespaces);
        return document.WithSyntaxRoot(newUnit).Project.Solution;
    }

    private static readonly SyntaxAnnotation Remove = new(nameof(UseModuleBaseCodeFixProvider));

    private static BaseListSyntax NewBaseList(BaseListSyntax baseList, ImmutableArray<BaseTypeSyntax> dropped)
    {
        var kept = baseList.Types.Where(type => !dropped.Contains(type)).Select(type => type.WithoutTrivia());
        BaseTypeSyntax moduleBase = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("ModuleBase"));

        return SyntaxFactory.BaseList(SyntaxFactory.SeparatedList([moduleBase, .. kept]))
            .WithColonToken(baseList.ColonToken)
            .NormalizeWhitespace()
            .WithLeadingTrivia(baseList.GetLeadingTrivia())
            .WithTrailingTrivia(baseList.GetTrailingTrivia());
    }

    /// <summary>A method written in place of <paramref name="replaced"/>, with its leading trivia.</summary>
    private static MemberDeclarationSyntax Member(MethodDeclarationSyntax replaced, string signature, string body, string memberIndent, string lineBreak)
    {
        var text = memberIndent + signature + lineBreak +
            memberIndent + "{" + lineBreak +
            ModuleEditing.Indent(body, memberIndent + "    ", lineBreak) + lineBreak +
            memberIndent + "}" + lineBreak;

        var member = SyntaxFactory.ParseMemberDeclaration(text)!;
        return member
            .WithLeadingTrivia(LeadingComments(replaced).AddRange(member.GetLeadingTrivia()))
            .WithTrailingTrivia(replaced.GetTrailingTrivia());
    }

    // The indentation is written into the new text; only what came before it — comments, blank
    // lines — is carried over.
    private static SyntaxTriviaList LeadingComments(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        return trivia.Count > 0 && trivia.Last().IsKind(SyntaxKind.WhitespaceTrivia)
            ? SyntaxFactory.TriviaList(trivia.Take(trivia.Count - 1))
            : trivia;
    }

    /// <summary>What the fix does to this class; null when it cannot.</summary>
    private static FixPlan? Plan(ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        var compilation = model.Compilation;
        var hostingStartup = compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.HostingStartupMetadataName);
        var startupFilter = compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.StartupFilterMetadataName);
        var webHostBuilder = compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.WebHostBuilderMetadataName);
        if (hostingStartup is null || webHostBuilder is null || ModuleFacts.GetModuleBaseType(compilation) is null ||
            declaration.BaseList is not { } baseList ||
            model.GetDeclaredSymbol(declaration, cancellationToken) is not { } type ||
            type.BaseType?.SpecialType != SpecialType.System_Object ||
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            declaration.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return null;
        }

        var dropped = baseList.Types
            .Where(baseType =>
                model.GetTypeInfo(baseType.Type, cancellationToken).Type is { } baseSymbol &&
                (SymbolEqualityComparer.Default.Equals(baseSymbol, hostingStartup) ||
                 SymbolEqualityComparer.Default.Equals(baseSymbol, startupFilter)))
            .ToImmutableArray();
        if (!dropped.Any(baseType => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(baseType.Type, cancellationToken).Type, hostingStartup)))
        {
            return null;
        }

        // The methods that implement the interfaces, found as the runtime finds them rather than by name.
        var hostingConfigure = Implementation(declaration, type, hostingStartup, cancellationToken);
        var implementsFilter = startupFilter is not null && type.Interfaces.Contains(startupFilter, SymbolEqualityComparer.Default);
        var filterConfigure = implementsFilter ? Implementation(declaration, type, startupFilter!, cancellationToken) : null;
        if (hostingConfigure is null || (implementsFilter && filterConfigure is null))
        {
            return null;
        }

        // The overrides the fix writes must not meet a member of the same name — a private
        // Configure(IApplicationBuilder) helper, say, which the filter may well be calling.
        if (declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                method != hostingConfigure && method != filterConfigure &&
                method.Identifier.Text is "Configure" or "ConfigureServices"))
        {
            return null;
        }

        if (Services(hostingConfigure, type, model, cancellationToken) is not { } services)
        {
            return null;
        }

        if (filterConfigure is null)
        {
            return new FixPlan(dropped, hostingConfigure, services.Builder, services.Body, null, null, null);
        }

        if (Pipeline(filterConfigure, model, cancellationToken) is not { } pipeline)
        {
            return null;
        }

        return new FixPlan(dropped, hostingConfigure, services.Builder, services.Body, filterConfigure, pipeline.App, pipeline.Body);
    }

    /// <summary>The declaration of the method that implements <paramref name="interfaceType"/>'s <c>Configure</c>, if it is in this class.</summary>
    private static MethodDeclarationSyntax? Implementation(
        ClassDeclarationSyntax declaration, INamedTypeSymbol type, INamedTypeSymbol interfaceType, CancellationToken cancellationToken) =>
        interfaceType.GetMembers("Configure").FirstOrDefault() is { } member &&
        type.FindImplementationForInterfaceMember(member) is { DeclaringSyntaxReferences: { Length: 1 } references } &&
        references[0].GetSyntax(cancellationToken) is MethodDeclarationSyntax method &&
        method.Parent == declaration
            ? method
            : null;

    /// <summary>
    /// The services the module registers, rewritten for the builder: <c>Configure(IWebHostBuilder)</c>
    /// may only call <c>builder.ConfigureServices(lambda)</c>.
    /// </summary>
    private static (string Builder, string Body)? Services(MethodDeclarationSyntax configure, INamedTypeSymbol module, SemanticModel model, CancellationToken cancellationToken)
    {
        var parameter = model.GetDeclaredSymbol(configure, cancellationToken)!.Parameters[0];

        IEnumerable<ExpressionSyntax> calls;
        if (configure.Body is { } body)
        {
            if (!body.Statements.All(statement => statement is ExpressionStatementSyntax))
            {
                return null;
            }

            calls = body.Statements.Cast<ExpressionStatementSyntax>().Select(statement => statement.Expression);
        }
        else if (configure.ExpressionBody is { } arrow)
        {
            calls = [arrow.Expression];
        }
        else
        {
            return null;
        }

        var lambdas = new List<(LambdaExpressionSyntax Lambda, IParameterSymbol? Context, IParameterSymbol Services)>();
        foreach (var call in calls)
        {
            if (call is not InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureServices", Expression: IdentifierNameSyntax receiver },
                    ArgumentList.Arguments: { Count: 1 } arguments,
                } ||
                arguments[0].Expression is not LambdaExpressionSyntax lambda ||
                !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, parameter))
            {
                return null;
            }

            var lambdaParameters = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => [simple.Parameter],
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToArray(),
                _ => [],
            };
            if (lambdaParameters.Length is not (1 or 2))
            {
                return null;
            }

            var context = lambdaParameters.Length == 2 ? model.GetDeclaredSymbol(lambdaParameters[0], cancellationToken) : null;
            var services = model.GetDeclaredSymbol(lambdaParameters[lambdaParameters.Length - 1], cancellationToken);
            if (services is null)
            {
                return null;
            }

            lambdas.Add((lambda, context, services));
        }

        // One method in place of several lambdas: a return in any but the last would now skip the
        // others, and locals of the same name in two of them would collide.
        if (BuilderRewriting.PickName(lambdas.Select(entry => entry.Lambda.Body)) is not { } builder ||
            lambdas.Take(lambdas.Count - 1).Any(entry => BuilderRewriting.Returns(entry.Lambda.Body)))
        {
            return null;
        }

        var names = lambdas.Select(entry => BuilderRewriting.DeclaredNames(entry.Lambda.Body)).ToArray();
        var collide = names.SelectMany(set => set).GroupBy(name => name).Any(group => group.Count() > 1);

        var parts = new List<string>();
        foreach (var (lambda, context, services) in lambdas)
        {
            if (BuilderRewriting.Rewrite(lambda.Body, context, services, builder, model, module, cancellationToken) is not { } rewritten)
            {
                return null;
            }

            if (rewritten.Length > 0)
            {
                parts.Add(collide ? BuilderRewriting.AsBlock(rewritten) : rewritten);
            }
        }

        return (builder, string.Join("\n", parts));
    }

    /// <summary>
    /// The module's middleware from an <c>IStartupFilter.Configure</c> of the usual shape:
    /// <c>return app =&gt; { next(app); … };</c>, or the same as an expression body.
    /// </summary>
    private static (string App, string Body)? Pipeline(MethodDeclarationSyntax configure, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetDeclaredSymbol(configure, cancellationToken) is not { } symbol ||
            model.Compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.ApplicationBuilderMetadataName) is not { } applicationBuilder)
        {
            return null;
        }

        var next = symbol.Parameters[0];
        var returned = configure.ExpressionBody?.Expression ??
            (configure.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax { Expression: { } value } ? value : null);
        if (returned is not SimpleLambdaExpressionSyntax lambda ||
            model.GetDeclaredSymbol(lambda.Parameter, cancellationToken) is not { } app ||
            !SymbolEqualityComparer.Default.Equals(app.Type, applicationBuilder))
        {
            return null;
        }

        bool IsNextCall(SyntaxNode node) =>
            node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax callee, ArgumentList.Arguments: { Count: 1 } arguments } &&
            arguments[0].Expression is IdentifierNameSyntax argument &&
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(callee, cancellationToken).Symbol, next) &&
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(argument, cancellationToken).Symbol, app);

        string body;
        if (lambda.Body is BlockSyntax block)
        {
            if (block.Statements.Count == 0 || block.Statements[0] is not ExpressionStatementSyntax first || !IsNextCall(first.Expression))
            {
                return null;
            }

            var rest = block.Statements.Skip(1).ToArray();
            body = rest.Length == 0
                ? string.Empty
                : BuilderRewriting.Outdent(block.SyntaxTree.GetText().ToString(
                    Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(rest[0].FullSpan.Start, rest[rest.Length - 1].FullSpan.End)));

            if (rest.Any(statement => statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, next))))
            {
                return null;
            }
        }
        else if (IsNextCall(lambda.Body))
        {
            body = string.Empty;
        }
        else
        {
            return null;
        }

        return (lambda.Parameter.Identifier.Text, body);
    }

    private sealed class FixPlan(
        ImmutableArray<BaseTypeSyntax> dropped,
        MethodDeclarationSyntax hostingConfigure,
        string builder,
        string services,
        MethodDeclarationSyntax? filterConfigure,
        string? app,
        string? pipeline)
    {
        public ImmutableArray<BaseTypeSyntax> Dropped { get; } = dropped;

        public MethodDeclarationSyntax HostingConfigure { get; } = hostingConfigure;

        public string Builder { get; } = builder;

        public string Services { get; } = services;

        public MethodDeclarationSyntax? FilterConfigure { get; } = filterConfigure;

        public string? App { get; } = app;

        public string? Pipeline { get; } = pipeline;
    }
}
