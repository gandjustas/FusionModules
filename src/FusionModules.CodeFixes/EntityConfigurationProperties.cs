using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FusionModules.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.CodeFixes;

/// <summary>
/// A context's <c>DbSet&lt;T&gt;</c> properties become extension members of <c>DbContext</c>, in the
/// context's namespace: <c>context.Orders</c> reads as it did, and the context no longer names the
/// entity's type. The second half of MOD0016's fix — by the time it runs, the entity's
/// configuration class has said what the property said to the model.
/// </summary>
/// <remarks>
/// <para>
/// With C# 14 the member is an extension property and its uses stay as they are, with two
/// exceptions the language makes: an expression tree may not read an extension property, so a use
/// inside one — a subquery in a <c>Where</c>, a compiled query — becomes <c>context.Set&lt;T&gt;()</c>,
/// which EF Core reads as it read the property; and a member of the context itself has to say
/// <c>this.Orders</c>. Before C# 14, in the context's project or in one that uses the property, the
/// member is an extension method and every use gets its parentheses.
/// </para>
/// <para>
/// A property that is assigned, named by <c>nameof</c> or reached through <c>base</c> stays: an
/// extension member can be none of those. So does one whose name another namespace has given to an
/// extension member of <c>DbContext</c> already — a second context's <c>Players</c> — where some
/// code would find both: the language calls that ambiguous. How a property is used is read here,
/// across the solution, and not by the analyzer.
/// </para>
/// </remarks>
internal static class EntityConfigurationProperties
{
    /// <summary>The properties that go, and as what.</summary>
    internal sealed class Sets
    {
        public List<EntityConfigurations.PropertyPlan> Properties { get; } = [];

        /// <summary>Extension methods rather than properties: some project that would read them is before C# 14.</summary>
        public bool Methods { get; set; }
    }

    /// <summary>One place a property is read.</summary>
    private sealed class Use(Document document, TextSpan span, string? text, bool inTree, bool import)
    {
        public Document Document { get; } = document;

        public TextSpan Span { get; } = span;

        /// <summary>What the place says instead; null when it stays as written.</summary>
        public string? Text { get; } = text;

        public bool InTree { get; } = inTree;

        /// <summary>Whether the document has to import the context's namespace to find the member.</summary>
        public bool Import { get; } = import;
    }

    /// <summary>An extension member of <c>DbContext</c> that the solution declares.</summary>
    private sealed class Rival(ISymbol symbol, string ns)
    {
        public ISymbol Symbol { get; } = symbol;

        public string Namespace { get; } = ns;

        public string Assembly { get; } = symbol.ContainingAssembly.Identity.Name;

        /// <summary>Whether code at the position finds the member: its assembly, its accessibility, its namespace.</summary>
        public bool SeenAt(SemanticModel model, int position, CancellationToken cancellationToken)
        {
            var compilation = model.Compilation;
            var own = compilation.Assembly.Identity.Name == Assembly;
            if (!own && !compilation.SourceModule.ReferencedAssemblySymbols.Any(assembly => assembly.Identity.Name == Assembly))
            {
                return false;
            }

            for (ISymbol? symbol = Symbol; symbol is not null and not INamespaceSymbol; symbol = symbol.ContainingSymbol)
            {
                if (!own && symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.NotApplicable))
                {
                    return false;
                }
            }

            return Namespace.Length == 0 || Sees(model, position, Namespace, cancellationToken);
        }
    }

    /// <summary>The entity as the property's declaration names it.</summary>
    public static string EntityText(PropertyDeclarationSyntax declaration)
    {
        var type = declaration.Type is NullableTypeSyntax nullable ? nullable.ElementType : declaration.Type;
        var generic = type as GenericNameSyntax ?? (type as QualifiedNameSyntax)?.Right as GenericNameSyntax;
        return generic?.TypeArgumentList.Arguments[0].ToString() ?? string.Empty;
    }

    /// <summary>Which of the properties the analysis let go the solution's code lets go too.</summary>
    public static async Task<Sets> SelectAsync(Document document, EntityConfigurations.Plan plan, CancellationToken cancellationToken)
    {
        var sets = new Sets { Methods = !Modern(document.Project) };
        var ns = Namespace(plan.Context);
        var candidates = plan.Properties.Where(property => property.Left is null).ToList();
        var solution = document.Project.Solution;
        var rivals = await RivalsAsync(
            solution, new HashSet<string>(candidates.Select(property => property.Symbol.Name), System.StringComparer.Ordinal), ns, plan.Context.ContainingAssembly.Identity.Name, cancellationToken)
            .ConfigureAwait(false);
        foreach (var property in candidates)
        {
            if (Member(plan.Context.ContainingAssembly, property.Symbol.Name, ns, cancellationToken) is { } member &&
                !SymbolEqualityComparer.Default.Equals(member.Entity, property.Entity))
            {
                // The namespace has an extension member of that name for something else.
                continue;
            }

            var named = rivals.Where(rival => rival.Symbol.Name == property.Symbol.Name).ToList();
            if (await UsesAsync(solution, property.Symbol, property.Entity, ns, methods: false, named, cancellationToken).ConfigureAwait(false) is { } uses &&
                !await CrossesAsync(solution, document.Project, plan.Context, ns, named, cancellationToken).ConfigureAwait(false))
            {
                sets.Methods |= uses.Any(use => !use.InTree && !Modern(use.Document.Project));
                sets.Properties.Add(property);
            }
        }

        return sets;
    }

    /// <summary>
    /// Removes the properties, writes their extension members into <c>{Context}Extensions.cs</c>
    /// beside the context, and changes the uses that have to change.
    /// </summary>
    public static async Task<Solution> RemoveAsync(
        Solution solution,
        DocumentId contextDocument,
        EntityConfigurations.Plan plan,
        Sets sets,
        IReadOnlyCollection<string> usings,
        bool fileScoped,
        string unit,
        string lineBreak,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        if (sets.Properties.Count == 0 ||
            solution.GetProject(contextDocument.ProjectId) is not { } project ||
            await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
        {
            return solution;
        }

        var ns = Namespace(plan.Context);
        var context = compilation.GetTypeByMetadataName(ns.Length > 0 ? ns + "." + plan.Context.MetadataName : plan.Context.MetadataName);
        if (context is null)
        {
            return solution;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var declarations = new Dictionary<DocumentId, List<TextSpan>>();
        var imports = new HashSet<DocumentId>();
        var members = new List<(string Name, string[] Lines)>();
        var removed = false;
        foreach (var name in sets.Properties.Select(property => property.Symbol.Name))
        {
            if (context.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is not { DeclaringSyntaxReferences.Length: 1 } property ||
                (property.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() is not INamedTypeSymbol entity ||
                await property.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is not PropertyDeclarationSyntax declaration ||
                solution.GetDocument(declaration.SyntaxTree) is not { } home ||
                await UsesAsync(solution, property, entity, ns, sets.Methods, [], cancellationToken).ConfigureAwait(false) is not { } uses)
            {
                continue;
            }

            var text = await home.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var leading = LeadingStart(declaration);
            Add(declarations, home.Id, TextSpan.FromBounds(text.Lines.GetLineFromPosition(leading).Start, text.Lines.GetLineFromPosition(declaration.Span.End).EndIncludingLineBreak));
            foreach (var use in uses)
            {
                if (use.Text is not null)
                {
                    Add(changes, use.Document.Id, new TextChange(use.Span, use.Text));
                }

                if (use.Import)
                {
                    imports.Add(use.Document.Id);
                }
            }

            removed = true;
            if (Member(context.ContainingAssembly, name, ns, cancellationToken) is { } member &&
                SymbolEqualityComparer.Default.Equals(member.Entity, entity) && member.Method == sets.Methods)
            {
                // Another context of the namespace had the same set, and its member serves this one too.
                continue;
            }

            var entityText = EntityText(declaration);
            var comments = EntityConfigurationSplit.Extract(text, TextSpan.FromBounds(leading, declaration.SpanStart), []);
            var line = sets.Methods
                ? $"public static DbSet<{entityText}> {name}(this DbContext context) => context.Set<{entityText}>();"
                : $"public DbSet<{entityText}> {name} => context.Set<{entityText}>();";
            members.Add((name, comments.Length > 0 ? [.. comments.Split('\n'), line] : [line]));
        }

        if (!removed)
        {
            return solution;
        }

        foreach (var pair in declarations)
        {
            var text = await solution.GetDocument(pair.Key)!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var span in WithBlankLines(text, pair.Value))
            {
                Add(changes, pair.Key, new TextChange(span, string.Empty));
            }
        }

        foreach (var pair in changes)
        {
            var text = await solution.GetDocument(pair.Key)!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            solution = solution.WithDocumentText(pair.Key, text.WithChanges(pair.Value.GroupBy(change => change.Span).Select(group => group.First()).OrderBy(change => change.Span.Start)));
        }

        if (ns.Length > 0)
        {
            foreach (var id in imports)
            {
                solution = await EntityConfigurationSplit.AddUsingsAsync(solution, id, [$"using {ns};"], cancellationToken, prune: false).ConfigureAwait(false);
            }
        }

        return members.Count == 0
            ? solution
            : await WriteMembersAsync(solution, contextDocument, plan, context, members, sets.Methods, usings, ns, fileScoped, unit, lineBreak, encoding, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// An extension member of <c>DbContext</c> of that name that the project declares already in the
    /// context's namespace: the entity its set is of, and whether it is a method.
    /// </summary>
    private static (ITypeSymbol? Entity, bool Method)? Member(IAssemblySymbol assembly, string name, string ns, CancellationToken cancellationToken)
    {
        foreach (var type in Types(assembly.GlobalNamespace).Where(type => type.IsStatic && Namespace(type) == ns))
        {
            foreach (var member in Named(type, name))
            {
                if (SetOf(member, cancellationToken) is { } set)
                {
                    return ((set as INamedTypeSymbol)?.TypeArguments.FirstOrDefault(), member is IMethodSymbol);
                }
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns) =>
        ns.GetTypeMembers().Concat(ns.GetNamespaceMembers().SelectMany(Types));

    /// <summary>The members of that name a static class declares, in its extension blocks too.</summary>
    private static IEnumerable<ISymbol> Named(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).Concat(type.GetTypeMembers().SelectMany(nested => nested.GetMembers(name)));

    /// <summary>What an extension member of <c>DbContext</c> returns; null for any other member.</summary>
    private static ITypeSymbol? SetOf(ISymbol member, CancellationToken cancellationToken) =>
        member switch
        {
            IMethodSymbol { IsExtensionMethod: true, Parameters: { Length: 1 } parameters } method when parameters[0].Type.Name == "DbContext" => method.ReturnType,
            IPropertySymbol property when member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken).Parent is TypeDeclarationSyntax block &&
                                          block.Kind().ToString().StartsWith("Extension", System.StringComparison.Ordinal) => property.Type,
            _ => null,
        };

    /// <summary>
    /// The extension members of <c>DbContext</c> with one of the names that the solution declares
    /// anywhere but in the context's namespace of the context's project: a member of the context's
    /// own would be found beside each of them wherever both namespaces are.
    /// </summary>
    private static async Task<List<Rival>> RivalsAsync(Solution solution, HashSet<string> names, string ns, string assembly, CancellationToken cancellationToken)
    {
        var rivals = new List<Rival>();
        if (names.Count == 0)
        {
            return rivals;
        }

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp ||
                await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            foreach (var type in Types(compilation.Assembly.GlobalNamespace).Where(type => type.IsStatic))
            {
                var home = Namespace(type);
                if (home == ns && compilation.Assembly.Identity.Name == assembly)
                {
                    continue;
                }

                foreach (var name in type.MemberNames.Concat(type.GetTypeMembers().SelectMany(nested => nested.MemberNames)).Where(names.Contains).Distinct().ToList())
                {
                    rivals.AddRange(Named(type, name).Where(member => SetOf(member, cancellationToken) is not null).Select(member => new Rival(member, home)));
                }
            }
        }

        return rivals;
    }

    /// <summary>
    /// Whether some code reads one of the other members where it would find the context's too: the
    /// other side of what <see cref="Classify"/> asks of the property's own uses.
    /// </summary>
    private static async Task<bool> CrossesAsync(
        Solution solution, Project home, INamedTypeSymbol context, string ns, List<Rival> rivals, CancellationToken cancellationToken)
    {
        if (rivals.Count == 0)
        {
            return false;
        }

        var graph = solution.GetProjectDependencyGraph();
        var open = context.DeclaredAccessibility == Accessibility.Public;
        foreach (var rival in rivals)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(rival.Symbol, solution, cancellationToken).ConfigureAwait(false))
            {
                foreach (var location in reference.Locations)
                {
                    var project = location.Document.Project;
                    if (project.Id != home.Id && !(open && graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id).Contains(home.Id)))
                    {
                        continue;
                    }

                    if (ns.Length == 0 ||
                        await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model ||
                        Sees(model, location.Location.SourceSpan.Start, ns, cancellationToken))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void Add<T>(Dictionary<DocumentId, List<T>> map, DocumentId id, T value)
    {
        if (!map.TryGetValue(id, out var list))
        {
            map.Add(id, list = []);
        }

        list.Add(value);
    }

    /// <summary>
    /// The lines of the removed declarations with the blank lines that only set them apart: none is
    /// left at the top of the class or above its closing brace, and one of two around a removed group goes.
    /// </summary>
    private static IEnumerable<TextSpan> WithBlankLines(SourceText text, List<TextSpan> spans)
    {
        var merged = new List<TextSpan>();
        foreach (var span in spans.OrderBy(span => span.Start))
        {
            if (merged.Count > 0 && merged[merged.Count - 1].End >= span.Start)
            {
                merged[merged.Count - 1] = TextSpan.FromBounds(merged[merged.Count - 1].Start, System.Math.Max(merged[merged.Count - 1].End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }

        foreach (var span in merged)
        {
            var first = text.Lines.GetLineFromPosition(span.Start).LineNumber;
            var last = text.Lines.GetLineFromPosition(System.Math.Max(span.Start, span.End - 1)).LineNumber;
            var before = first > 0 ? text.Lines[first - 1].ToString().Trim() : null;
            var after = last + 1 < text.Lines.Count ? text.Lines[last + 1].ToString().Trim() : null;
            if (after is { Length: 0 } && before is "{" or { Length: 0 })
            {
                yield return TextSpan.FromBounds(span.Start, text.Lines[last + 1].EndIncludingLineBreak);
            }
            else if (before is { Length: 0 } && after is not null && after.StartsWith("}", System.StringComparison.Ordinal))
            {
                yield return TextSpan.FromBounds(text.Lines[first - 1].Start, span.End);
            }
            else
            {
                yield return span;
            }
        }
    }

    /// <summary>
    /// Every place the property is read, with what it has to say once the property is an extension
    /// member; null when one of them cannot be said that way, and the property stays.
    /// </summary>
    private static async Task<List<Use>?> UsesAsync(
        Solution solution, IPropertySymbol property, INamedTypeSymbol entity, string ns, bool methods, List<Rival> rivals, CancellationToken cancellationToken)
    {
        var uses = new List<Use>();
        var seen = new HashSet<(DocumentId, TextSpan)>();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(property, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var location in reference.Locations)
            {
                if (!seen.Add((location.Document.Id, location.Location.SourceSpan)))
                {
                    continue;
                }

                if (location.IsImplicit ||
                    location.Document is SourceGeneratedDocument ||
                    await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root ||
                    await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model ||
                    Classify(location.Document, model, root.FindNode(location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true), entity, ns, methods, cancellationToken) is not { } use)
                {
                    return null;
                }

                if (!use.InTree && rivals.Any(rival => rival.SeenAt(model, location.Location.SourceSpan.Start, cancellationToken)))
                {
                    // Another namespace has an extension member of that name, and this place finds it.
                    return null;
                }

                uses.Add(use);
            }
        }

        return uses;
    }

    private static Use? Classify(Document document, SemanticModel model, SyntaxNode node, INamedTypeSymbol entity, string ns, bool methods, CancellationToken cancellationToken)
    {
        if (node is not IdentifierNameSyntax name || name.IsPartOfStructuredTrivia())
        {
            return null;
        }

        var access = name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : null;
        var conditional = name.Parent as MemberBindingExpressionSyntax;
        ExpressionSyntax whole = (ExpressionSyntax?)access ?? (ExpressionSyntax?)conditional ?? name;
        if (access?.Expression is BaseExpressionSyntax ||
            name.Parent is NameColonSyntax or NameEqualsSyntax ||
            (whole.Parent is AssignmentExpressionSyntax assignment && assignment.Left == whole) ||
            whole.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 } ||
            whole.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
        {
            return null;
        }

        var receiver = access?.Expression.ToString() ?? "this";
        var inTree = conditional is null && InExpressionTree(whole, model, cancellationToken);
        if (inTree)
        {
            // An expression tree may not read an extension property, and EF Core translates no call of
            // an extension method on a query's own parameter: Set<T>() is what both of them read.
            return new Use(document, whole.Span, $"{receiver}.Set<{entity.ToMinimalDisplayString(model, whole.SpanStart)}>()", inTree: true, import: false);
        }

        var import = ns.Length > 0 && !Sees(model, whole.SpanStart, ns, cancellationToken);
        if (methods)
        {
            return access is null && conditional is null
                ? new Use(document, name.Span, $"this.{name.Identifier.ValueText}()", inTree: false, import)
                : new Use(document, new TextSpan(name.Span.End, 0), "()", inTree: false, import);
        }

        return access is null && conditional is null
            ? new Use(document, name.Span, $"this.{name.Identifier.ValueText}", inTree: false, import)
            : new Use(document, name.Span, null, inTree: false, import);
    }

    /// <summary>
    /// Whether the code is compiled into an expression tree: inside a lambda converted to one, or in
    /// a clause of a query expression. A query's first source is not; any other part is taken to be,
    /// since <c>Set&lt;T&gt;()</c> reads the same wherever it stands.
    /// </summary>
    private static bool InExpressionTree(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var ancestor in expression.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax lambda &&
                model.GetTypeInfo(lambda, cancellationToken).ConvertedType is INamedTypeSymbol { Name: "Expression", ContainingNamespace: { Name: "Expressions", ContainingNamespace.Name: "Linq" } })
            {
                return true;
            }

            if (ancestor is QueryExpressionSyntax query && !query.FromClause.Expression.Span.Contains(expression.Span))
            {
                return true;
            }

            if (ancestor is MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>Whether code at the position finds the members of the namespace: it is inside it, or imports it.</summary>
    private static bool Sees(SemanticModel model, int position, string ns, CancellationToken cancellationToken)
    {
        for (var enclosing = model.GetEnclosingSymbol(position, cancellationToken)?.ContainingNamespace; enclosing is { IsGlobalNamespace: false }; enclosing = enclosing.ContainingNamespace)
        {
            if (enclosing.ToDisplayString() == ns)
            {
                return true;
            }
        }

        return model.GetImportScopes(position, cancellationToken)
            .SelectMany(scope => scope.Imports)
            .Any(import => import.NamespaceOrType is INamespaceSymbol imported && imported.ToDisplayString() == ns);
    }

    private static async Task<Solution> WriteMembersAsync(
        Solution solution,
        DocumentId contextDocument,
        EntityConfigurations.Plan plan,
        INamedTypeSymbol context,
        List<(string Name, string[] Lines)> members,
        bool methods,
        IReadOnlyCollection<string> usings,
        string ns,
        bool fileScoped,
        string unit,
        string lineBreak,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(contextDocument)!;
        var project = document.Project;
        var className = plan.Context.Name + "Extensions";
        var prefix = EntityConfigurationSplit.PathPrefix(document);
        var fileName = className + ".cs";
        var existing = project.Documents.FirstOrDefault(candidate =>
            prefix is not null && candidate.FilePath is { } path
                ? string.Equals(path, prefix + fileName, System.StringComparison.OrdinalIgnoreCase)
                : candidate.Name.Equals(fileName, System.StringComparison.OrdinalIgnoreCase) && candidate.Folders.SequenceEqual(document.Folders));

        if (existing is not null &&
            await existing.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root &&
            root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(type => type.Identifier.ValueText == className) is { } type)
        {
            // A second run, or a second context of the namespace: the members that are not there yet.
            var text = await existing.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var there = new HashSet<string>(
                type.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(property => property.Identifier.ValueText)
                    .Concat(type.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(method => method.Identifier.ValueText)),
                System.StringComparer.Ordinal);
            var missing = members.Where(member => !there.Contains(member.Name)).ToList();
            if (missing.Count == 0)
            {
                return solution;
            }

            var indent = ModuleEditing.IndentationOf(type);
            var block = methods ? null : type.Members.OfType<TypeDeclarationSyntax>().FirstOrDefault(member => member.Kind().ToString().StartsWith("Extension", System.StringComparison.Ordinal));
            var fileBreak = ModuleEditing.LineBreakOf(root);
            string added;
            int at;
            if (block is not null)
            {
                added = Indent(missing.SelectMany(member => member.Lines), indent + unit + unit);
                at = text.Lines.GetLineFromPosition(block.CloseBraceToken.SpanStart).Start;
            }
            else
            {
                var body = methods
                    ? string.Join("\n\n", missing.Select(member => Indent(member.Lines, indent + unit)))
                    : Indent(Block(missing.SelectMany(member => member.Lines), unit), indent + unit);
                added = (type.Members.Count > 0 ? "\n" : string.Empty) + body;
                at = text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).Start;
            }

            solution = solution.WithDocumentText(existing.Id, text.WithChanges(new TextChange(new TextSpan(at, 0), (added + "\n").Replace("\n", fileBreak))));
            return await EntityConfigurationSplit.AddUsingsAsync(solution, existing.Id, usings, cancellationToken).ConfigureAwait(false);
        }

        var isPublic = context.DeclaredAccessibility == Accessibility.Public;
        string Render(IEnumerable<string> directives)
        {
            var lines = new List<string>(directives);
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

            lines.Add($"{indent}{(isPublic ? "public" : "internal")} static class {className}");
            lines.Add($"{indent}{{");
            lines.Add(methods
                ? string.Join("\n\n", members.Select(member => Indent(member.Lines, indent + unit)))
                : Indent(Block(members.SelectMany(member => member.Lines), unit), indent + unit));
            lines.Add($"{indent}}}");
            if (ns.Length > 0 && !fileScoped)
            {
                lines.Add("}");
            }

            return (string.Join("\n", lines) + "\n").Replace("\n", lineBreak);
        }

        // Unless the file is there under another class's name: then beside it, under a name of its own.
        if (existing is not null)
        {
            fileName = EntityConfigurationSplit.FileName(project, document.Folders, prefix, className);
        }

        var draft = project.AddDocument(fileName, SourceText.From(Render(usings), encoding), document.Folders, prefix is null ? null : prefix + fileName);
        var unused = await EntityConfigurationSplit.UnusedUsingsAsync(draft, cancellationToken).ConfigureAwait(false);
        return draft.WithText(SourceText.From(Render(usings.Where(directive => !unused.Contains(directive))), encoding)).Project.Solution;
    }

    /// <summary>The members as an extension block of <c>DbContext</c>.</summary>
    private static IEnumerable<string> Block(IEnumerable<string> members, string unit) =>
        ["extension(DbContext context)", "{", .. members.Select(line => line.Length == 0 ? line : unit + line), "}"];

    private static string Indent(IEnumerable<string> lines, string indent) =>
        string.Join("\n", lines.Select(line => line.Length == 0 ? line : indent + line));

    /// <summary>Where a declaration's own comments begin: documentation and plain ones, with no blank line between.</summary>
    private static int LeadingStart(MemberDeclarationSyntax declaration)
    {
        var start = declaration.SpanStart;
        var trivia = declaration.GetLeadingTrivia();
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
            else if (current.IsKind(SyntaxKind.SingleLineCommentTrivia) || current.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                     current.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || current.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                start = current.FullSpan.Start;
                commented = true;
            }
            else
            {
                break;
            }
        }

        return start;
    }

    private static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;

    /// <summary>Whether the project's language has extension properties: C# 14.</summary>
    private static bool Modern(Project project) =>
        project.ParseOptions is CSharpParseOptions options && options.LanguageVersion.MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp14;
}
