using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// The fix for MOD0015: the program's own registrations and endpoints move into a module of the
/// same project, so that <c>dotnet format</c> can run the conversion.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ProgramToModuleCodeFixProvider)), Shared]
public sealed class ProgramToModuleCodeFixProvider : CodeFixProvider
{
    internal const string Title = "Move this project's own registrations into a module";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.ProgramCanBecomeModuleId];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => ConvertAll.Instance;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        // The diagnostic is the gate; the plan, with the solution in view, is made once, when the
        // fix runs. It may find that a project reference the analyzer counted is shared, and leave
        // the solution as it was.
        foreach (var diagnostic in context.Diagnostics)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => ProgramToModule.ConvertAsync(context.Document, cancellationToken),
                    equivalenceKey: nameof(ProgramToModuleCodeFixProvider)),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One document after another. The batch fixer merges text changes and cannot add the
    /// <c>Module.cs</c> a conversion creates — and <c>dotnet format</c> fixes everything through
    /// this. A second document of the same project, a <c>Startup.cs</c> beside a <c>Program.cs</c>,
    /// adds to the module the first one created.
    /// </summary>
    private sealed class ConvertAll : FixAllProvider
    {
        public static readonly ConvertAll Instance = new();

        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() =>
            [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var documents = new List<DocumentId>();
            if (fixAllContext.Scope == FixAllScope.Document)
            {
                if (fixAllContext.Document is { } document)
                {
                    documents.Add(document.Id);
                }
            }
            else
            {
                var projects = fixAllContext.Scope == FixAllScope.Solution
                    ? fixAllContext.Solution.Projects
                    : [fixAllContext.Project];
                foreach (var project in projects)
                {
                    var diagnostics = await fixAllContext.GetAllDiagnosticsAsync(project).ConfigureAwait(false);
                    documents.AddRange(diagnostics
                        .Select(diagnostic => project.GetDocument(diagnostic.Location.SourceTree)?.Id)
                        .OfType<DocumentId>()
                        .Distinct());
                }
            }

            if (documents.Count == 0)
            {
                return null;
            }

            return CodeAction.Create(
                Title,
                async cancellationToken =>
                {
                    var solution = fixAllContext.Solution;
                    foreach (var id in documents)
                    {
                        if (solution.GetDocument(id) is { } document)
                        {
                            solution = await ProgramToModule.ConvertAsync(document, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    return solution;
                },
                equivalenceKey: nameof(ProgramToModuleCodeFixProvider));
        }
    }
}
