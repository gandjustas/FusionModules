using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionModules.CodeFixes;

/// <summary>
/// MOD0015's conversion as a refactoring, on <c>Program.cs</c> or <c>Startup.cs</c> in the IDE:
/// wherever the caret is in the top-level statements, in <c>Main</c>, or in a startup's
/// <c>ConfigureServices</c> or <c>Configure</c>.
/// </summary>
/// <remarks>
/// Offered more widely than the diagnostic is reported: also in a host that composes modules
/// already, or has a module class of its own — the statements are then added to it. A refactoring
/// is asked for; a diagnostic is not. Offered on the analyzer's cheap answer, asked at every caret;
/// the conversion, with the solution in view, is planned once, when it runs.
/// </remarks>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(ProgramToModuleRefactoringProvider)), Shared]
public sealed class ProgramToModuleRefactoringProvider : CodeRefactoringProvider
{
    /// <inheritdoc />
    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var document = context.Document;
        if (await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
        {
            return;
        }

        var node = root.FindToken(context.Span.Start).Parent;
        var inStartupCode = node?.AncestorsAndSelf().Any(ancestor =>
            ancestor is GlobalStatementSyntax ||
            (ancestor is MethodDeclarationSyntax method && method.Identifier.Text is "Main" or "ConfigureServices" or "Configure")) == true;
        if (!inStartupCode || !await ProgramToModule.MayConvertAsync(document, context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        context.RegisterRefactoring(CodeAction.Create(
            ProgramToModuleCodeFixProvider.Title,
            cancellationToken => ProgramToModule.ConvertAsync(document, cancellationToken),
            equivalenceKey: nameof(ProgramToModuleRefactoringProvider)));
    }
}
