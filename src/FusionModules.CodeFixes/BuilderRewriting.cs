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
    private const string WebHostEnvironmentMetadataName = "Microsoft.AspNetCore.Hosting.IWebHostEnvironment";
    private const string StartupFilterMetadataName = "Microsoft.AspNetCore.Hosting.IStartupFilter";

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
        if (body.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.Text == builder))
        {
            return null;
        }

        var webEnvironment = model.Compilation.GetTypeByMetadataName(WebHostEnvironmentMetadataName);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
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
        var unchained = new HashSet<SyntaxNode>();
        if (module is not null)
        {
            foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (!RegistersAsStartupFilter(invocation, module, model, cancellationToken))
                {
                    continue;
                }

                if (invocation == body)
                {
                    return string.Empty;
                }

                if (invocation.Parent is ExpressionStatementSyntax statement)
                {
                    dropped.Add(statement);
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
        }

        // Inner nodes are replaced first, so a dropped or unchained call sees its arguments and
        // receiver already rewritten.
        var rewritten = body.ReplaceNodes(replacements.Keys.Concat(dropped).Concat(unchained), (original, current) =>
            dropped.Contains(original) ? current.WithAdditionalAnnotations(Drop) :
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
        InvocationExpressionSyntax invocation, INamedTypeSymbol module, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            !method.Name.StartsWith("Add", System.StringComparison.Ordinal) ||
            model.Compilation.GetTypeByMetadataName(StartupFilterMetadataName) is not { } startupFilter ||
            !method.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, startupFilter)))
        {
            return false;
        }

        return method.TypeArguments.Any(argument => SymbolEqualityComparer.Default.Equals(argument, module)) ||
            invocation.ArgumentList.Arguments.Any(argument => argument.Expression switch
            {
                ThisExpressionSyntax => true,
                ObjectCreationExpressionSyntax creation =>
                    SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(creation, cancellationToken).Type, module),
                _ => false,
            });
    }

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
            return model.GetSymbolInfo(outer, cancellationToken).Symbol?.ContainingType is { } owner &&
                SymbolEqualityComparer.Default.Equals(owner, webEnvironment);
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
