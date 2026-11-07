using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.CodeFixes;

/// <summary>
/// The edits more than one fix makes.
/// </summary>
internal static class SyntaxEditing
{
    /// <summary>
    /// Adds <c>[assembly: <paramref name="attribute"/>]</c> after the usings, before everything
    /// else, which is where a reader expects it.
    /// </summary>
    /// <param name="document">The document to change.</param>
    /// <param name="unit">Its root.</param>
    /// <param name="attribute">An attribute list from <see cref="SyntaxGenerator.Attribute(string, SyntaxNode[])"/>,
    /// whose name is fully qualified: an assembly-level attribute sits above any file-scoped
    /// namespace, where a bare name would not bind.</param>
    public static Document AddAssemblyAttribute(Document document, CompilationUnitSyntax unit, SyntaxNode attribute)
    {
        var list = ((AttributeListSyntax)attribute)
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .NormalizeWhitespace(elasticTrivia: true)
            // Set after normalisation, which discards trivia applied before it. One line break:
            // whatever separated the usings from the first declaration is still there below.
            // Taken from the document rather than left to the formatter, whose default is the
            // machine's: a line feed injected into a carriage-return line feed file, or the other
            // way round, leaves a mixed-ending line nobody asked for.
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(LineBreakOf(unit)));

        return document.WithSyntaxRoot(unit.AddAttributeLists(list));
    }

    private static string LineBreakOf(SyntaxNode root)
    {
        var text = root.GetText();

        return text.Lines.Count > 1
            ? text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak))
            : Environment.NewLine;
    }
}
