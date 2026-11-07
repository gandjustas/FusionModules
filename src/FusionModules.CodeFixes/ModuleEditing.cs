using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionModules.CodeFixes;

/// <summary>
/// What code fixes that edit a module class need: finding one of its overrides, adding statements
/// to it, and the <c>using</c> directives those statements need.
/// </summary>
internal static class ModuleEditing
{
    public const string HostApplicationBuilderMetadataName = "Microsoft.Extensions.Hosting.IHostApplicationBuilder";

    /// <summary>The class's override of <paramref name="name"/> taking one parameter of the given type, if any.</summary>
    public static MethodDeclarationSyntax? FindOverride(
        ClassDeclarationSyntax declaration, SemanticModel model, string name, string parameterTypeMetadataName, CancellationToken cancellationToken)
    {
        var parameterType = model.Compilation.GetTypeByMetadataName(parameterTypeMetadataName);

        return declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method =>
                method.Identifier.Text == name &&
                method.Modifiers.Any(SyntaxKind.OverrideKeyword) &&
                method.ParameterList.Parameters.Count == 1 &&
                model.GetDeclaredSymbol(method, cancellationToken) is { } symbol &&
                SymbolEqualityComparer.Default.Equals(symbol.Parameters[0].Type, parameterType));
    }

    /// <summary>
    /// Adds statements at the end of an override, writing the override when there is none and
    /// turning an expression body into a block when there is.
    /// </summary>
    /// <remarks>
    /// Written as text, in the document's own line break and indentation, rather than through the
    /// formatter: the formatter's line break is the machine's, and a file that uses line feeds
    /// would end up with mixed endings in exactly the lines the fix added.
    /// </remarks>
    /// <param name="declaration">The class.</param>
    /// <param name="existing">The override, if the class has one.</param>
    /// <param name="signature">The override's signature, for when it is written new — without a body.</param>
    /// <param name="defaultParameterName">Its parameter's name when it is written new.</param>
    /// <param name="statements">The statements, given the parameter's name: each without indentation
    /// on its first line, later lines indented relative to it.</param>
    /// <param name="lineBreak">The document's line break.</param>
    public static ClassDeclarationSyntax AppendToOverride(
        ClassDeclarationSyntax declaration,
        MethodDeclarationSyntax? existing,
        string signature,
        string defaultParameterName,
        System.Func<string, IEnumerable<string>> statements,
        string lineBreak)
    {
        var memberIndent = MemberIndent(declaration);
        var statementIndent = memberIndent + "    ";

        string Lines(IEnumerable<string> code) =>
            string.Concat(code.Select(statement => Indent(statement, statementIndent, lineBreak) + lineBreak));

        if (existing is null)
        {
            var text = (declaration.Members.Count > 0 ? lineBreak : string.Empty) +
                memberIndent + signature + lineBreak +
                memberIndent + "{" + lineBreak +
                Lines(statements(defaultParameterName)) +
                memberIndent + "}" + lineBreak;

            return declaration.AddMembers(SyntaxFactory.ParseMemberDeclaration(text)!);
        }

        var parameter = existing.ParameterList.Parameters[0].Identifier.Text;
        if (existing.Body is { } body)
        {
            // Spliced in as text, before the closing brace's line, so that comments and directives
            // in what is added stay where they were written.
            var method = existing.ToFullString();
            var close = body.CloseBraceToken.SpanStart - existing.FullSpan.Start;
            var before = method.Substring(0, close);
            var lineStart = before.LastIndexOf('\n') + 1;
            var spliced = before.Substring(lineStart).Trim().Length == 0
                ? before.Substring(0, lineStart) + Lines(statements(parameter)) + before.Substring(lineStart) + method.Substring(close)
                : before.TrimEnd() + lineBreak + Lines(statements(parameter)) + memberIndent + method.Substring(close);

            return declaration.ReplaceNode(existing, SyntaxFactory.ParseMemberDeclaration(spliced)!);
        }

        // Expression-bodied: the expression becomes the first statement of a block.
        var arrow = existing.ExpressionBody!;
        var full = existing.ToFullString();
        var header = full.Substring(0, arrow.ArrowToken.SpanStart - existing.FullSpan.Start).TrimEnd();
        var expression = Dedent(arrow.Expression.ToString(), Column(arrow.Expression));

        var rewritten = header + lineBreak +
            memberIndent + "{" + lineBreak +
            Lines(new[] { expression + ";" }.Concat(statements(parameter))) +
            memberIndent + "}" + existing.GetTrailingTrivia().ToFullString();

        return declaration.ReplaceNode(existing, SyntaxFactory.ParseMemberDeclaration(rewritten)!);
    }

    /// <summary>The indentation of a class's members: its first member's, or the class's own plus four spaces.</summary>
    public static string MemberIndent(ClassDeclarationSyntax declaration) =>
        declaration.Members.FirstOrDefault() is { } member
            ? IndentationOf(member)
            : IndentationOf(declaration) + "    ";

    /// <summary>The whitespace before a node on its line.</summary>
    public static string IndentationOf(SyntaxNode node)
    {
        var text = node.SyntaxTree.GetText();
        var line = text.Lines.GetLineFromPosition(node.SpanStart);
        var prefix = text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(line.Start, node.SpanStart));
        return prefix.Length - prefix.TrimStart().Length == prefix.Length ? prefix : string.Empty;
    }

    /// <summary>The column a node starts at.</summary>
    public static int Column(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Character;

    /// <summary>Indents every line of <paramref name="code"/>, written in the given line break.</summary>
    public static string Indent(string code, string indent, string lineBreak) =>
        string.Join(lineBreak, Lines(code).Select(line => line.Length == 0 || IsDirective(line) ? line : indent + line));

    /// <summary>A preprocessor directive line — <c>#pragma</c>, <c>#if</c> — which stays at column zero.</summary>
    public static bool IsDirective(string line) => line.TrimStart().StartsWith("#", System.StringComparison.Ordinal);

    /// <summary>
    /// Removes <paramref name="column"/> columns of leading whitespace from every line after the
    /// first — the code started at that column, and its later lines were indented to match.
    /// </summary>
    public static string Dedent(string code, int column)
    {
        var lines = Lines(code);
        for (var i = 1; i < lines.Length; i++)
        {
            var whitespace = lines[i].Length - lines[i].TrimStart().Length;
            lines[i] = lines[i].Substring(System.Math.Min(whitespace, column));
        }

        return string.Join("\n", lines);
    }

    private static string[] Lines(string code) => code.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Adds a <c>using</c> for each namespace the file does not import already — itself, or through
    /// a global using anywhere in the compilation, which is where implicit usings live.
    /// </summary>
    public static CompilationUnitSyntax EnsureUsings(CompilationUnitSyntax unit, Compilation compilation, IEnumerable<string> namespaces)
    {
        var imported = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.GetRoot() is CompilationUnitSyntax other)
            {
                foreach (var directive in other.Usings.Where(u => u.GlobalKeyword != default && u.Alias is null && u.StaticKeyword == default))
                {
                    imported.Add(NameOf(directive));
                }
            }
        }

        foreach (var directive in unit.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is null && directive.StaticKeyword == default)
            {
                imported.Add(NameOf(directive));
            }
        }

        var missing = namespaces.Distinct().Where(ns => ns.Length > 0 && !imported.Contains(ns)).OrderBy(ns => ns, System.StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return unit;
        }

        var lineBreak = LineBreakOf(unit);
        var directives = missing.Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(lineBreak)));

        return unit.AddUsings(directives.ToArray());
    }

    /// <summary>The line break a document already uses, so an edit does not mix endings.</summary>
    public static string LineBreakOf(SyntaxNode root)
    {
        var text = root.GetText();
        return text.Lines.Count > 1
            ? text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak))
            : System.Environment.NewLine;
    }

    private static string NameOf(UsingDirectiveSyntax directive)
    {
        var name = directive.Name?.ToString() ?? string.Empty;
        return name.StartsWith("global::", System.StringComparison.Ordinal) ? name.Substring("global::".Length) : name;
    }
}
