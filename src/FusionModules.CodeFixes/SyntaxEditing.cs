using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

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
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(ModuleEditing.LineBreakOf(unit)));

        return document.WithSyntaxRoot(unit.AddAttributeLists(list));
    }

    /// <summary>
    /// Adds <c>using <paramref name="namespaceName"/>;</c> after the existing usings, unless the
    /// namespace is already in scope at <paramref name="position"/> — by a using in the file, a
    /// global using or an implicit one.
    /// </summary>
    /// <param name="unit">The root to change.</param>
    /// <param name="model">A semantic model of the document <paramref name="unit"/> came from.</param>
    /// <param name="position">Where the namespace's members are used, in that document.</param>
    /// <param name="namespaceName">The namespace to import.</param>
    public static CompilationUnitSyntax AddUsingIfMissing(
        CompilationUnitSyntax unit,
        SemanticModel model,
        int position,
        string namespaceName) =>
        ModuleEditing.EnsureUsings(unit, model, position, [namespaceName]);
}
