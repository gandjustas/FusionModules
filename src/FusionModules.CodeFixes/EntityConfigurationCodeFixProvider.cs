using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using FusionModules.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace FusionModules.CodeFixes;

/// <summary>
/// The fix for MOD0016: the entity configuration of a context's <c>OnModelCreating</c> moves into
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> classes, one file per entity.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EntityConfigurationCodeFixProvider)), Shared]
public sealed class EntityConfigurationCodeFixProvider : CodeFixProvider
{
    internal const string Title = "Move entity configuration into IEntityTypeConfiguration classes";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.EntityConfigurationCanMoveId];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => SplitAll.Instance;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            var span = diagnostic.Location.SourceSpan;
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => EntityConfigurationSplit.SplitAsync(context.Document, span, cancellationToken),
                    equivalenceKey: nameof(EntityConfigurationCodeFixProvider)),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One document after another, and one context after another in each. The batch fixer merges
    /// text changes and cannot add the files a split creates — and <c>dotnet format</c> fixes
    /// everything through this. A later context sees the classes an earlier one was given, so two
    /// contexts that configure one entity do not get one class name.
    /// </summary>
    private sealed class SplitAll : FixAllProvider
    {
        public static readonly SplitAll Instance = new();

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
                            solution = await EntityConfigurationSplit.SplitAsync(document, at: null, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    return solution;
                },
                equivalenceKey: nameof(EntityConfigurationCodeFixProvider));
        }
    }
}
