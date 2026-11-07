using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionModules.CodeFixes;

/// <summary>
/// Rewrites code written against <c>(WebHostBuilderContext context, IServiceCollection services)</c>
/// for <c>IHostApplicationBuilder</c>: the service collection becomes <c>builder.Services</c>,
/// <c>context.Configuration</c> becomes <c>builder.Configuration</c>, and
/// <c>context.HostingEnvironment</c> becomes <c>builder.Environment</c> — cast to
/// <c>IWebHostEnvironment</c> where the code needs the web environment's own members.
/// </summary>
/// <remarks>
/// Any other use of the context — passing it whole to a helper, say — has no counterpart on the
/// builder, and the rewrite gives up; so does it when the code already uses the builder's name.
/// </remarks>
internal static class BuilderRewriting
{

    private static readonly SyntaxAnnotation Drop = new(nameof(BuilderRewriting) + ".Drop");

    /// <summary>
    /// The statements of <paramref name="body"/> rewritten for the builder, outdented, with
    /// <c>\n</c> line breaks; null when they cannot be.
    /// </summary>
    /// <param name="body">A block, or the expression of an expression body.</param>
    /// <param name="context">The context parameter, if the code had one.</param>
    /// <param name="services">The service collection parameter.</param>
    /// <param name="builder">The builder's name.</param>
    /// <param name="model">A semantic model of the document <paramref name="body"/> is in.</param>
    /// <param name="module">When given, registrations of this module as an <c>IStartupFilter</c> are
    /// dropped: <c>ModuleBase</c> makes them itself.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static string? Rewrite(
        SyntaxNode body,
        IParameterSymbol? context,
        IParameterSymbol services,
        string builder,
        SemanticModel model,
        INamedTypeSymbol? module,
        CancellationToken cancellationToken)
    {
        // base.ConfigureServices(context, services) — what the IDE's override template writes —
        // calls an empty method, and the builder overload has no base call to keep. It goes, and
        // the context it passes whole does not count against the rewrite.
        var moduleBase = FusionModules.Analyzers.ModuleFacts.GetModuleBaseType(model.Compilation);
        var baseCalls = new HashSet<SyntaxNode>(body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(invocation =>
            invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } &&
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol { Name: "ConfigureServices" } called &&
            SymbolEqualityComparer.Default.Equals(called.ContainingType, moduleBase)));

        var webEnvironment = model.Compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.WebHostEnvironmentMetadataName);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Ancestors().Any(baseCalls.Contains))
            {
                continue;
            }

            var referenced = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (SymbolEqualityComparer.Default.Equals(referenced, services))
            {
                replacements[identifier] = Expression($"{builder}.Services", identifier);
            }
            else if (context is not null && SymbolEqualityComparer.Default.Equals(referenced, context))
            {
                if (identifier.Parent is not MemberAccessExpressionSyntax access || access.Expression != identifier)
                {
                    return null;
                }

                switch (access.Name.Identifier.Text)
                {
                    case "Configuration":
                        replacements[access] = Expression($"{builder}.Configuration", access);
                        break;
                    case "HostingEnvironment":
                        replacements[access] = Expression(
                            NeedsWebEnvironment(access, model, webEnvironment, cancellationToken)
                                ? $"((Microsoft.AspNetCore.Hosting.IWebHostEnvironment){builder}.Environment)"
                                : $"{builder}.Environment",
                            access);
                        break;
                    default:
                        return null;
                }
            }
        }

        var dropped = new HashSet<SyntaxNode>();
        var emptied = new HashSet<SyntaxNode>();
        var unchained = new HashSet<SyntaxNode>();
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (!baseCalls.Contains(invocation) && (module is null || !RegistersAsStartupFilter(invocation, module, model, cancellationToken)))
            {
                continue;
            }

            if (invocation == body)
            {
                return string.Empty;
            }

            if (invocation.Parent is ExpressionStatementSyntax statement)
            {
                // A statement in a block goes; one that is the body of an if or a loop becomes {}.
                (statement.Parent is BlockSyntax or SwitchSectionSyntax ? dropped : emptied).Add(statement);
            }
            else if (invocation.Expression is MemberAccessExpressionSyntax)
            {
                // Chained: services.AddSingleton<IStartupFilter>(this).AddX() keeps AddX on services.
                unchained.Add(invocation);
            }
            else
            {
                return null;
            }
        }

        // Inner nodes are replaced first, so a dropped or unchained call sees its arguments and
        // receiver already rewritten.
        var rewritten = body.ReplaceNodes(replacements.Keys.Concat(dropped).Concat(emptied).Concat(unchained), (original, current) =>
            dropped.Contains(original) ? current.WithAdditionalAnnotations(Drop) :
            emptied.Contains(original) ? SyntaxFactory.ParseStatement("{ }").WithTriviaFrom(current) :
            unchained.Contains(original) ? ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)current).Expression).Expression.WithTriviaFrom(current) :
            replacements[original]);
        rewritten = rewritten.RemoveNodes(rewritten.GetAnnotatedNodes(Drop), SyntaxRemoveOptions.KeepNoTrivia)!;

        if (rewritten is BlockSyntax block)
        {
            var inner = block.ToFullString();
            var start = block.OpenBraceToken.Span.End - block.FullSpan.Start;
            var end = block.CloseBraceToken.SpanStart - block.FullSpan.Start;
            return Outdent(inner.Substring(start, end - start));
        }

        return ModuleEditing.Dedent(rewritten.ToString(), ModuleEditing.Column(body)) + ";";
    }

    /// <summary>
    /// <c>AddSingleton&lt;IStartupFilter&gt;(this)</c>, <c>AddSingleton&lt;IStartupFilter&gt;(new Module())</c>
    /// or <c>AddSingleton&lt;IStartupFilter, Module&gt;()</c>: the module registering itself as a
    /// startup filter.
    /// </summary>
    public static bool RegistersAsStartupFilter(
        InvocationExpressionSyntax invocation, INamedTypeSymbol module, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetOperation(invocation, cancellationToken) is Microsoft.CodeAnalysis.Operations.IInvocationOperation operation &&
        FusionModules.Analyzers.Operations.RegistersAsStartupFilter(
            operation, model.Compilation.GetTypeByMetadataName(FusionModules.Analyzers.ModuleFacts.StartupFilterMetadataName), module);

    /// <summary>
    /// A name for the builder parameter that nothing in <paramref name="bodies"/> uses already, so
    /// that no lambda parameter or local of that name captures what the rewrite writes.
    /// </summary>
    public static string? PickName(IEnumerable<SyntaxNode> bodies)
    {
        var used = new HashSet<string>(
            bodies.SelectMany(body => body.DescendantTokens()).Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.Text),
            System.StringComparer.Ordinal);

        return new[] { "builder", "hostBuilder", "applicationBuilder" }.FirstOrDefault(name => !used.Contains(name));
    }

    /// <summary>
    /// Whether <paramref name="body"/> returns from itself — a <c>return</c> not inside a nested
    /// lambda, anonymous method or local function. Moved in front of other code, it would skip it.
    /// </summary>
    public static bool Returns(SyntaxNode body) =>
        body.DescendantNodes(node => node == body || node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<ReturnStatementSyntax>()
            .Any();

    /// <summary>The names <paramref name="body"/> declares: locals, pattern and out variables, loop variables, local functions.</summary>
    public static HashSet<string> DeclaredNames(SyntaxNode body) =>
        new(
            body.DescendantNodes().Select(node => node switch
            {
                VariableDeclaratorSyntax declarator => declarator.Identifier.Text,
                SingleVariableDesignationSyntax designation => designation.Identifier.Text,
                ForEachStatementSyntax loop => loop.Identifier.Text,
                LocalFunctionStatementSyntax function => function.Identifier.Text,
                CatchDeclarationSyntax { Identifier.Text: { Length: > 0 } name } => name,
                _ => null,
            }).OfType<string>(),
            System.StringComparer.Ordinal);

    /// <summary>Statements as their own block, so that their locals cannot collide with their neighbours'.</summary>
    public static string AsBlock(string statements) =>
        "{" + NewLine + string.Join(NewLine, statements.Split(NewLine[0]).Select(line =>
            line.Length == 0 || ModuleEditing.IsDirective(line) ? line : "    " + line)) + NewLine + "}";

    // The rewrite's own line break; the fixes turn it into the document's when they write.
    private const string NewLine = "\n";

    private static ExpressionSyntax Expression(string text, SyntaxNode replaced) =>
        SyntaxFactory.ParseExpression(text).WithTriviaFrom(replaced);

    /// <summary>
    /// Whether this use of <c>context.HostingEnvironment</c> needs <c>IWebHostEnvironment</c> rather
    /// than the builder's <c>IHostEnvironment</c>: a member only the web environment has, or a
    /// conversion to it.
    /// </summary>
    private static bool NeedsWebEnvironment(MemberAccessExpressionSyntax access, SemanticModel model, INamedTypeSymbol? webEnvironment, CancellationToken cancellationToken)
    {
        if (webEnvironment is null)
        {
            return false;
        }

        if (access.Parent is MemberAccessExpressionSyntax outer && outer.Expression == access)
        {
            var member = model.GetSymbolInfo(outer, cancellationToken).Symbol;
            var owner = member is IMethodSymbol { ReducedFrom: { } extension }
                ? extension.Parameters[0].Type
                : member?.ContainingType;
            return SymbolEqualityComparer.Default.Equals(owner, webEnvironment);
        }

        return SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(access, cancellationToken).ConvertedType, webEnvironment);
    }

    /// <summary>The block's inside without its blank first and last lines, its indentation removed.</summary>
    public static string Outdent(string inner)
    {
        var lines = inner.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        // Directives sit at column zero by convention; they neither set the indentation nor take it.
        var indent = lines.Where(line => line.Trim().Length > 0 && !ModuleEditing.IsDirective(line))
            .Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(line =>
            line.Trim().Length == 0 ? string.Empty :
            ModuleEditing.IsDirective(line) ? line.TrimStart() :
            line.Substring(System.Math.Min(indent, line.Length - line.TrimStart().Length))));
    }
}
