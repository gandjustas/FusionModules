using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FusionModules.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.CodeFixes;

/// <summary>
/// Moves the entity configuration of a context's <c>OnModelCreating</c> into
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> classes, as <see cref="EntityConfigurations"/> planned it.
/// </summary>
/// <remarks>
/// <para>
/// One class per entity, each in a file of its own: beside the project's other configuration
/// classes when it has some, beside the context otherwise. The code is moved as text — comments,
/// line breaks and indentation as they were written — and the file is given the context file's
/// usings, less those it does not need.
/// </para>
/// <para>
/// Where nothing scans the assembly for configurations, the first statement of each entity becomes
/// <c>ApplyConfiguration(new …())</c>, so the entities are still configured in the order they were.
/// Where the context scans it — <c>ApplyModuleConfigurations</c> in a module,
/// <c>ApplyConfigurationsFromAssembly</c> — the statements are removed and the scan finds the class.
/// </para>
/// </remarks>
internal static class EntityConfigurationSplit
{
    private const string EntityFrameworkCore = "Microsoft.EntityFrameworkCore";
    private const string Builders = "Microsoft.EntityFrameworkCore.Metadata.Builders";

    /// <summary>
    /// Splits the context whose <c>OnModelCreating</c> is at <paramref name="at"/>, or every context
    /// of the document.
    /// </summary>
    public static async Task<Solution> SplitAsync(Document document, TextSpan? at, CancellationToken cancellationToken)
    {
        var solution = document.Project.Solution;
        for (var round = 0; round < 64; round++)
        {
            if (solution.GetDocument(document.Id) is not { } current ||
                await current.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root ||
                await current.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model ||
                EntityConfigurations.Types.For(model.Compilation) is not { } types)
            {
                return solution;
            }

            EntityConfigurations.Plan? plan = null;
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(EntityConfigurations.MightApply))
            {
                if (at is { } span && !method.Span.IntersectsWith(span))
                {
                    continue;
                }

                plan = EntityConfigurations.Analyze(model, method, types, cancellationToken);
                if (plan is not null)
                {
                    break;
                }
            }

            if (plan is null)
            {
                return solution;
            }

            var changed = await ApplyAsync(current, root, types, plan, cancellationToken).ConfigureAwait(false);
            if (changed is null)
            {
                return solution;
            }

            solution = changed;
            if (at is not null)
            {
                return solution;
            }
        }

        return solution;
    }

    /// <summary>A class to write: a new one, or statements for one that exists.</summary>
    private sealed class Target(EntityConfigurations.Move move, string builder, string body)
    {
        public EntityConfigurations.Move Move { get; } = move;

        public string Builder { get; } = builder;

        /// <summary>The statements of <c>Configure</c>, not indented, with line feeds.</summary>
        public string Body { get; } = body;

        public string ClassName { get; set; } = string.Empty;
    }

    private static async Task<Solution?> ApplyAsync(
        Document document, CompilationUnitSyntax root, EntityConfigurations.Types types, EntityConfigurations.Plan plan, CancellationToken cancellationToken)
    {
        var project = document.Project;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return null;
        }

        var lineBreak = ModuleEditing.LineBreakOf(root);
        var contextNamespace = plan.Context.ContainingNamespace is { IsGlobalNamespace: false } declared ? declared.ToDisplayString() : string.Empty;
        var place = Place(document, types, contextNamespace);

        var targets = new List<Target>();
        foreach (var move in plan.Moves)
        {
            var parameter = move.Existing is null
                ? null
                : EntityConfigurations.ConfigureOf(move.Existing, move.Entity, cancellationToken)?.ParameterList.Parameters[0].Identifier.ValueText;
            if ((move.Existing is not null && parameter is null) || BuilderName(move, parameter) is not { } builder)
            {
                continue;
            }

            var parts = new List<string>();
            var emitted = new HashSet<EntityConfigurations.Item>();
            foreach (var piece in move.Pieces)
            {
                foreach (var local in piece.Pulled.Where(emitted.Add))
                {
                    parts.Add(Render(text, local.Piece!, builder, plan.Context.Name));
                }

                parts.Add(Render(text, piece, builder, plan.Context.Name));
            }

            targets.Add(new Target(move, builder, string.Join("\n", parts.Where(part => part.Length > 0))));
        }

        if (targets.Count == 0)
        {
            return null;
        }

        // The context: each moved statement goes, and the first of an entity that nothing would
        // find otherwise becomes the call that applies its class.
        var taken = new HashSet<string>(System.StringComparer.Ordinal);
        var removed = new HashSet<SyntaxNode>();
        var replaced = new Dictionary<SyntaxNode, SyntaxNode>();
        var gone = new HashSet<MethodDeclarationSyntax>();
        foreach (var target in targets)
        {
            var move = target.Move;
            target.ClassName = move.Existing?.Name ?? ClassName(compilation, place.Namespace, move.Entity.Name, taken);
            for (var i = 0; i < move.Pieces.Count; i++)
            {
                var piece = move.Pieces[i];
                var node = piece.Item.Node;
                foreach (var local in piece.Pulled)
                {
                    removed.Add(local.Node);
                }

                if (piece.Helper is not null)
                {
                    gone.Add(piece.Helper);
                }

                if (i > 0 || !move.Apply)
                {
                    removed.Add(node);
                }
                else if (node is StatementSyntax statement)
                {
                    var comments = AttachedStart(statement);
                    replaced[node] = SyntaxFactory.ParseStatement($"{piece.Item.Builder}.ApplyConfiguration(new {target.ClassName}());")
                        .WithLeadingTrivia(statement.GetLeadingTrivia().Where(trivia => trivia.Span.End <= comments))
                        .WithTrailingTrivia(statement.GetTrailingTrivia());
                }
                else
                {
                    replaced[node] = SyntaxFactory.ParseExpression($"{piece.Item.Builder}.ApplyConfiguration(new {target.ClassName}())").WithTriviaFrom(node);
                }
            }
        }

        // A helper that only configured entities, and has nothing left to do, goes with its call.
        foreach (var helper in plan.Helpers)
        {
            if (helper.Items.Count > 0 && helper.Items.All(item => removed.Contains(item.Node)))
            {
                gone.Add(helper.Helper);
                removed.Add(helper.Call);
            }
        }

        var edits = new Dictionary<DocumentId, List<(SyntaxNode Node, System.Func<SyntaxNode, SyntaxNode?> Change)>>();
        void Edit(DocumentId id, SyntaxNode node, System.Func<SyntaxNode, SyntaxNode?> change)
        {
            if (!edits.TryGetValue(id, out var list))
            {
                edits.Add(id, list = []);
            }

            list.Add((node, change));
        }

        foreach (var method in new[] { plan.Method }.Concat(plan.Helpers.Select(helper => helper.Helper)).Where(method => !gone.Contains(method)))
        {
            if (method.Body is { } body && body.Statements.Any(statement => removed.Contains(statement) || replaced.ContainsKey(statement)))
            {
                Edit(document.Id, body, _ => Rebuild(body, removed, replaced, lineBreak));
            }
            else if (method.ExpressionBody is { } arrow && replaced.TryGetValue(arrow.Expression, out var replacement))
            {
                Edit(document.Id, arrow.Expression, _ => replacement);
            }
        }

        foreach (var method in gone)
        {
            Edit(document.Id, method, _ => null);
        }

        // The statements a class of the project already has room for.
        var wanted = Usings(root, plan, contextNamespace, place.Namespace);
        var appendedTo = new HashSet<DocumentId>();
        foreach (var target in targets.Where(target => target.Move.Existing is not null))
        {
            var configure = EntityConfigurations.ConfigureOf(target.Move.Existing!, target.Move.Entity, cancellationToken)!;
            if (project.GetDocument(configure.SyntaxTree) is not { } home)
            {
                return null;
            }

            var statements = target.Body;
            Edit(home.Id, configure.Body!, _ => Append(configure.Body!, statements, ModuleEditing.LineBreakOf(configure.SyntaxTree.GetRoot(cancellationToken))));
            appendedTo.Add(home.Id);
        }

        var solution = project.Solution;
        var before = await UnusedUsingsAsync(document, cancellationToken).ConfigureAwait(false);
        foreach (var pair in edits)
        {
            var edited = solution.GetDocument(pair.Key)!;
            var editedRoot = (await edited.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            var tracked = editedRoot.TrackNodes(pair.Value.Select(edit => edit.Node));
            foreach (var (node, change) in pair.Value)
            {
                if (tracked.GetCurrentNode(node) is not { } now)
                {
                    continue;
                }

                tracked = change(now) is { } changed
                    ? tracked.ReplaceNode(now, changed)
                    : tracked.RemoveNode(now, SyntaxRemoveOptions.KeepNoTrivia)!;
            }

            solution = await WriteAsync(solution, pair.Key, tracked, cancellationToken).ConfigureAwait(false);
        }

        // The usings the appended statements need, and no others.
        foreach (var id in appendedTo.Where(id => id != document.Id))
        {
            solution = await AddUsingsAsync(solution, id, wanted, cancellationToken).ConfigureAwait(false);
        }

        // The context names the new classes; and no longer needs the usings only the moved code did.
        var context = solution.GetDocument(document.Id)!;
        if (targets.Any(target => target.Move.Apply) && place.Namespace.Length > 0 && !Encloses(place.Namespace, contextNamespace))
        {
            solution = await AddUsingsAsync(solution, document.Id, [$"using {place.Namespace};"], cancellationToken, prune: false).ConfigureAwait(false);
            context = solution.GetDocument(document.Id)!;
        }

        var after = await UnusedUsingsAsync(context, cancellationToken).ConfigureAwait(false);
        after.ExceptWith(before);
        if (after.Count > 0)
        {
            var contextRoot = (CompilationUnitSyntax)(await context.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            solution = await WriteAsync(solution, document.Id, WithoutUsings(contextRoot, after), cancellationToken).ConfigureAwait(false);
        }

        // The new classes, each in its own file.
        var fileScoped = plan.Method.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().Any() ||
            (!plan.Method.Ancestors().OfType<NamespaceDeclarationSyntax>().Any() &&
             root.SyntaxTree.Options is CSharpParseOptions options &&
             options.LanguageVersion.MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp10);
        var unit = IndentUnit(plan.Method);
        foreach (var target in targets.Where(target => target.Move.Existing is null))
        {
            var entity = target.Move.Pieces[0].EntityName!.ToString();
            string Class(IEnumerable<string> usings) =>
                RenderClass(usings, place.Namespace, fileScoped, target.ClassName, entity, target.Builder, target.Body, unit, lineBreak);

            var current = solution.GetProject(project.Id)!;
            var fileName = FileName(current, place, target.ClassName);
            var draft = current.AddDocument(
                fileName,
                SourceText.From(Class(wanted), text.Encoding),
                place.Folders,
                place.PathPrefix is null ? null : place.PathPrefix + fileName);
            var unused = await UnusedUsingsAsync(draft, cancellationToken).ConfigureAwait(false);
            solution = draft.WithText(SourceText.From(Class(wanted.Where(directive => !unused.Contains(directive))), text.Encoding)).Project.Solution;
        }

        return solution;
    }

    /// <summary>The document with a new root, in the encoding it had: a byte order mark stays.</summary>
    private static async Task<Solution> WriteAsync(Solution solution, DocumentId id, SyntaxNode root, CancellationToken cancellationToken)
    {
        var encoding = (await solution.GetDocument(id)!.GetTextAsync(cancellationToken).ConfigureAwait(false)).Encoding;
        return solution.WithDocumentText(id, SourceText.From(root.ToFullString(), encoding));
    }

    /// <summary>
    /// Where the classes go. A project that keeps configuration classes has a place for them: the
    /// folder and namespace most of them are in. Otherwise beside the context, in its namespace.
    /// </summary>
    private static (string Namespace, IReadOnlyList<string> Folders, string? PathPrefix) Place(
        Document document, EntityConfigurations.Types types, string contextNamespace)
    {
        var convention = types.AllConfigurations
            .Where(type => type.ContainingType is null && type.DeclaringSyntaxReferences.Length == 1)
            .Select(type => (
                Namespace: type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
                Document: document.Project.GetDocument(type.DeclaringSyntaxReferences[0].SyntaxTree)))
            .Where(found => found.Document is not null && found.Document.Id != document.Id)
            .GroupBy(found => (found.Namespace, Prefix: PathPrefix(found.Document!) ?? string.Join("/", found.Document!.Folders)))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Prefix, System.StringComparer.Ordinal)
            .FirstOrDefault();

        return convention is null
            ? (contextNamespace, document.Folders, PathPrefix(document))
            : (convention.Key.Namespace, convention.First().Document!.Folders, PathPrefix(convention.First().Document!));
    }

    /// <summary>A document's path up to its file name, separators as they are.</summary>
    private static string? PathPrefix(Document document) =>
        document.FilePath is { Length: > 0 } path
            ? path.Substring(0, path.Length - System.IO.Path.GetFileName(path).Length)
            : null;

    /// <summary><c>OrderConfiguration</c>, or the next name no type of the namespace has.</summary>
    private static string ClassName(Compilation compilation, string ns, string entity, HashSet<string> taken)
    {
        bool Free(string name) =>
            compilation.GetTypeByMetadataName(ns.Length > 0 ? ns + "." + name : name) is null && !taken.Contains(name);

        var name = entity + "Configuration";
        if (!Free(name))
        {
            name = entity + "EntityConfiguration";
            for (var i = 2; !Free(name); i++)
            {
                name = entity + "EntityConfiguration" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        taken.Add(name);
        return name;
    }

    private static string FileName(Project project, (string Namespace, IReadOnlyList<string> Folders, string? PathPrefix) place, string className)
    {
        bool Taken(string name) => project.Documents.Any(document =>
            place.PathPrefix is not null && document.FilePath is { } path
                ? string.Equals(path, place.PathPrefix + name, System.StringComparison.OrdinalIgnoreCase)
                : document.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase) && document.Folders.SequenceEqual(place.Folders));

        var fileName = $"{className}.cs";
        for (var i = 2; Taken(fileName); i++)
        {
            fileName = $"{className}{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}.cs";
        }

        return fileName;
    }

    /// <summary>
    /// The name of the entity's builder in the class: the one the code already uses, when every
    /// moved statement can — it is that statement's own name for it, or a name the statement does
    /// not use for anything else. Null when a class that exists names its builder otherwise.
    /// </summary>
    private static string? BuilderName(EntityConfigurations.Move move, string? required)
    {
        var names = move.Pieces.ToDictionary(
            piece => piece,
            piece => new HashSet<string>(
                piece.Scope.DescendantTokens().Concat(piece.Pulled.SelectMany(local => local.Node.DescendantTokens()))
                    .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
                    .Select(token => token.ValueText),
                System.StringComparer.Ordinal));

        bool Fits(string candidate) => move.Pieces.All(piece => piece.Parameter?.Name == candidate || !names[piece].Contains(candidate));

        var preferred = required ?? move.Pieces.Select(piece => piece.Parameter?.Name).FirstOrDefault(name => name is not null) ?? "builder";
        if (Fits(preferred))
        {
            return preferred;
        }

        if (required is not null)
        {
            return null;
        }

        var candidates = new[] { "builder", "entity", "entityBuilder" }
            .Concat(Enumerable.Range(2, 100).Select(i => "builder" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return candidates.FirstOrDefault(Fits);
    }

    /// <summary>
    /// A statement as the class has it: the lambda's body, the helper's, or the chain with the
    /// entity's builder where <c>modelBuilder.Entity&lt;T&gt;()</c> was — under the comments that
    /// stood above the statement.
    /// </summary>
    private static string Render(SourceText text, EntityConfigurations.Piece piece, string builder, string context)
    {
        var edits = new List<(TextSpan Span, string Text)>();
        edits.AddRange(piece.Qualify.Select(position => (new TextSpan(position, 0), context + ".")));
        if (piece.Parameter is not null && piece.Parameter.Name != builder)
        {
            edits.AddRange(piece.ParameterUses.Select(span => (span, builder)));
        }

        if (piece.Root is not null)
        {
            edits.Add((piece.Root.Span, builder));
        }

        var scope = piece.Kind == EntityConfigurations.PieceKind.Lambda ? ((LambdaExpressionSyntax)piece.Scope).Body : piece.Scope;
        var (span, suffix) = scope switch
        {
            BlockSyntax block => (TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart), string.Empty),
            ExpressionSyntax expression => (expression.Span, ";"),
            _ => (scope.Span, string.Empty),
        };

        var code = Extract(text, span, edits) + suffix;
        var statement = piece.Item.Node as StatementSyntax;
        var comments = statement is null
            ? string.Empty
            : Extract(text, TextSpan.FromBounds(AttachedStart(statement), statement.SpanStart), []);
        return comments.Length > 0 ? comments + "\n" + code : code;
    }

    /// <summary>
    /// The text of a span with the edits made, without the indentation it had where it stood: its
    /// lines keep their indentation against each other. Line feeds, whatever the document has.
    /// </summary>
    private static string Extract(SourceText text, TextSpan span, List<(TextSpan Span, string Text)> edits)
    {
        var start = span.Start;
        var end = span.End;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (start == end)
        {
            return string.Empty;
        }

        var line = text.Lines.GetLineFromPosition(start);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, start));
        var baseline = prefix.Length - prefix.TrimStart().Length;

        var code = new StringBuilder(text.ToString(TextSpan.FromBounds(start, end)));
        foreach (var (at, replacement) in edits.Where(edit => edit.Span.Start >= start && edit.Span.End <= end).OrderByDescending(edit => edit.Span.Start))
        {
            code.Remove(at.Start - start, at.Length).Insert(at.Start - start, replacement);
        }

        var lines = code.ToString().Replace("\r\n", "\n").Split('\n');
        var strip = baseline;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                strip = System.Math.Min(strip, lines[i].Length - lines[i].TrimStart().Length);
            }
        }

        for (var i = 1; i < lines.Length; i++)
        {
            lines[i] = lines[i].Trim().Length == 0 ? string.Empty : lines[i].Substring(strip).TrimEnd();
        }

        return string.Join("\n", lines);
    }

    /// <summary>Where the comments directly above a statement begin: those with no blank line between.</summary>
    private static int AttachedStart(StatementSyntax statement)
    {
        var start = statement.SpanStart;
        var trivia = statement.GetLeadingTrivia();
        var commented = true;
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var current = trivia[i];
            if (current.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            if (current.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (!commented)
                {
                    break;
                }

                commented = false;
            }
            else if (current.IsKind(SyntaxKind.SingleLineCommentTrivia) || current.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                start = current.SpanStart;
                commented = true;
            }
            else
            {
                break;
            }
        }

        return start;
    }

    /// <summary>
    /// A method's block without the moved statements. A blank line that set a moved group apart
    /// still sets apart what follows it, and none is left at the top of the block.
    /// </summary>
    private static BlockSyntax Rebuild(BlockSyntax block, HashSet<SyntaxNode> removed, Dictionary<SyntaxNode, SyntaxNode> replaced, string lineBreak)
    {
        var statements = new List<StatementSyntax>();
        var dropped = false;
        var apart = false;
        foreach (var statement in block.Statements)
        {
            if (removed.Contains(statement))
            {
                apart |= StartsWithBlankLine(statement);
                dropped = true;
                continue;
            }

            var kept = replaced.TryGetValue(statement, out var replacement) ? (StatementSyntax)replacement : statement;
            if (dropped && statements.Count == 0)
            {
                while (StartsWithBlankLine(kept))
                {
                    var leading = kept.GetLeadingTrivia();
                    var lineEnd = leading.IndexOf(SyntaxKind.EndOfLineTrivia);
                    kept = kept.WithLeadingTrivia(leading.Skip(lineEnd + 1));
                }
            }
            else if (dropped && apart && !StartsWithBlankLine(kept))
            {
                kept = kept.WithLeadingTrivia(kept.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(lineBreak)));
            }

            dropped = false;
            apart = false;
            statements.Add(kept);
        }

        return block.WithStatements(SyntaxFactory.List(statements));
    }

    private static bool StartsWithBlankLine(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return true;
            }

            if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The block of a class's <c>Configure</c> with the statements after its own.</summary>
    private static BlockSyntax Append(BlockSyntax body, string statements, string lineBreak)
    {
        var indent = body.Statements.FirstOrDefault() is { } first
            ? ModuleEditing.IndentationOf(first)
            : ModuleEditing.IndentationOf(body.Parent!) + "    ";
        var parsed = (BlockSyntax)SyntaxFactory.ParseStatement(("{\n" + ModuleEditing.Indent(statements, indent, "\n") + "\n}").Replace("\n", lineBreak));
        return body.AddStatements([.. parsed.Statements]);
    }

    /// <summary>
    /// The usings the moved code was written under: the context file's, those of the namespaces
    /// around the context — which the code saw without a using — and EF Core's two for the class itself.
    /// </summary>
    private static List<string> Usings(CompilationUnitSyntax root, EntityConfigurations.Plan plan, string contextNamespace, string targetNamespace)
    {
        var directives = root.Usings
            .Concat(plan.Method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(ns => ns.Usings))
            .Where(directive => directive.GlobalKeyword.RawKind == 0)
            .Select(directive => directive.WithoutTrivia().ToString())
            .ToList();
        directives.Add($"using {EntityFrameworkCore};");
        directives.Add($"using {Builders};");

        if (contextNamespace != targetNamespace)
        {
            var parts = contextNamespace.Split('.');
            for (var i = 1; i <= parts.Length && contextNamespace.Length > 0; i++)
            {
                var enclosing = string.Join(".", parts.Take(i));
                if (!Encloses(enclosing, targetNamespace))
                {
                    directives.Add($"using {enclosing};");
                }
            }
        }

        return [.. directives
            .Distinct()
            .OrderBy(directive => directive.Contains("=") ? 2 : directive.StartsWith("using static ", System.StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(directive => directive.StartsWith("using System", System.StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(directive => directive.TrimEnd(';'), System.StringComparer.Ordinal)];
    }

    /// <summary>Whether code in <paramref name="inner"/> sees the members of <paramref name="outer"/> without a using.</summary>
    private static bool Encloses(string outer, string inner) =>
        inner == outer || inner.StartsWith(outer + ".", System.StringComparison.Ordinal);

    private static string RenderClass(
        IEnumerable<string> usings, string ns, bool fileScoped, string className, string entity, string builder, string body, string unit, string lineBreak)
    {
        var lines = new List<string>(usings);
        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        var indent = string.Empty;
        if (ns.Length > 0 && fileScoped)
        {
            lines.Add($"namespace {ns};");
            lines.Add(string.Empty);
        }
        else if (ns.Length > 0)
        {
            lines.Add($"namespace {ns}");
            lines.Add("{");
            indent = unit;
        }

        lines.Add($"{indent}internal sealed class {className} : IEntityTypeConfiguration<{entity}>");
        lines.Add($"{indent}{{");
        lines.Add($"{indent}{unit}public void Configure(EntityTypeBuilder<{entity}> {builder})");
        lines.Add($"{indent}{unit}{{");
        if (body.Length > 0)
        {
            lines.Add(ModuleEditing.Indent(body, indent + unit + unit, "\n"));
        }

        lines.Add($"{indent}{unit}}}");
        lines.Add($"{indent}}}");
        if (ns.Length > 0 && !fileScoped)
        {
            lines.Add("}");
        }

        return (string.Join("\n", lines) + "\n").Replace("\n", lineBreak);
    }

    /// <summary>What one level of indentation is in the context's file: a method's against its class.</summary>
    private static string IndentUnit(MethodDeclarationSyntax method)
    {
        var inner = ModuleEditing.IndentationOf(method);
        var outer = method.Parent is null ? string.Empty : ModuleEditing.IndentationOf(method.Parent);
        return inner.Length > outer.Length && inner.StartsWith(outer, System.StringComparison.Ordinal)
            ? inner.Substring(outer.Length)
            : "    ";
    }

    /// <summary>Adds the usings a document lacks and, unless told otherwise, takes back those it turns out not to need.</summary>
    private static async Task<Solution> AddUsingsAsync(
        Solution solution, DocumentId id, IReadOnlyCollection<string> wanted, CancellationToken cancellationToken, bool prune = true)
    {
        var document = solution.GetDocument(id)!;
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
        {
            return solution;
        }

        var present = new HashSet<string>(
            root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>()
                .Select(directive => directive.WithoutTrivia().ToString()),
            System.StringComparer.Ordinal);
        var missing = wanted.Where(directive => !present.Contains(directive)).ToList();
        if (missing.Count == 0)
        {
            return solution;
        }

        var lineBreak = ModuleEditing.LineBreakOf(root);
        var added = SyntaxFactory.ParseCompilationUnit(string.Join(lineBreak, missing) + lineBreak).Usings;
        var leading = root.Usings.Count == 0 ? root.GetLeadingTrivia() : default;
        var with = root.Usings.Count == 0
            ? root.WithoutLeadingTrivia().WithUsings(added.Replace(added[0], added[0].WithLeadingTrivia(leading)))
            : root.AddUsings([.. added]);
        solution = await WriteAsync(solution, id, with, cancellationToken).ConfigureAwait(false);
        if (!prune)
        {
            return solution;
        }

        var unused = await UnusedUsingsAsync(solution.GetDocument(id)!, cancellationToken).ConfigureAwait(false);
        unused.IntersectWith(missing);
        if (unused.Count == 0)
        {
            return solution;
        }

        var now = (CompilationUnitSyntax)(await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        return await WriteAsync(solution, id, WithoutUsings(now, unused), cancellationToken).ConfigureAwait(false);
    }

    private static CompilationUnitSyntax WithoutUsings(CompilationUnitSyntax root, HashSet<string> unused)
    {
        var directives = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(directive => unused.Contains(directive.WithoutTrivia().ToString()))
            .ToList();

        // A file's header is the first directive's leading trivia: it stays when the directive goes.
        var plain = directives.Where(directive => !directive.GetLeadingTrivia().Any(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))).ToList();
        var result = root.TrackNodes(directives);
        foreach (var directive in directives)
        {
            result = result.RemoveNode(
                result.GetCurrentNode(directive)!,
                plain.Contains(directive) ? SyntaxRemoveOptions.KeepNoTrivia : SyntaxRemoveOptions.KeepLeadingTrivia)!;
        }

        return result;
    }

    /// <summary>
    /// The usings of a document that the compiler finds unnecessary, or said twice. It reports
    /// them only where documentation comments are parsed, so the document is read that way.
    /// </summary>
    private static async Task<HashSet<string>> UnusedUsingsAsync(Document document, CancellationToken cancellationToken)
    {
        var unused = new HashSet<string>(System.StringComparer.Ordinal);
        if (await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is not { Options: CSharpParseOptions options } tree ||
            await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
        {
            return unused;
        }

        var parsed = options.DocumentationMode == DocumentationMode.None
            ? tree.WithRootAndOptions(await tree.GetRootAsync(cancellationToken).ConfigureAwait(false), options.WithDocumentationMode(DocumentationMode.Parse))
            : tree;
        if (parsed != tree)
        {
            compilation = compilation.ReplaceSyntaxTree(tree, parsed);
        }

        var root = await parsed.GetRootAsync(cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in compilation.GetSemanticModel(parsed).GetDiagnostics(cancellationToken: cancellationToken))
        {
            if (diagnostic.Id is "CS8019" or "CS8933" or "CS0105" &&
                root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>() is { } directive)
            {
                unused.Add(directive.WithoutTrivia().ToString());
            }
        }

        return unused;
    }
}
