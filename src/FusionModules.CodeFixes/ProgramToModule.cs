using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using FusionModules.Analyzers;

namespace FusionModules.CodeFixes;

/// <summary>
/// The conversion behind MOD0015's fix and the refactoring: a program's own registrations and
/// endpoint mappings move into a module class of the same project.
/// </summary>
/// <remarks>
/// <para>
/// What moves is <see cref="ProgramConversion"/>'s answer, asked with the solution in view: the
/// project's own code is its source and the project references that no other program of the
/// solution references, directly or not. A program that references this one — its tests, an Aspire
/// AppHost — is not counted against it, nor is a test project. With no other program in view, as
/// when <c>dotnet format</c> is given the one project, a reference cannot be told from a shared
/// library, and only the project's source counts.
/// </para>
/// <para>
/// The module is <c>Module.cs</c> beside the project file, <c>internal sealed class Module : ModuleBase</c>
/// in the project's root namespace — the name every sample's module has; another name only when a type
/// called <c>Module</c> is there already. A project that has a module class gets the statements added
/// to it instead.
/// </para>
/// </remarks>
internal static class ProgramToModule
{
    private const string ModuleClassName = "Module";

    /// <summary>What would move out of the document, with the solution in view; null when nothing would.</summary>
    public static async Task<ConversionPlan?> PlanAsync(Document document, CancellationToken cancellationToken)
    {
        if (await PrepareAsync(document, cancellationToken).ConfigureAwait(false) is not (var root, var model, var types))
        {
            return null;
        }

        var (isOwn, ownProjects) = await OwnCodeAsync(document.Project, model.Compilation, cancellationToken).ConfigureAwait(false);
        var compilations = new Dictionary<string, Compilation>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var id in ownProjects)
        {
            if (document.Project.Solution.GetProject(id) is { } project &&
                await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation)
            {
                compilations[project.AssemblyName] = compilation;
            }
        }

        return ProgramConversion.Analyze(model, root, types, isOwn, cancellationToken, method => Sources(method, compilations, cancellationToken));
    }

    /// <summary>
    /// Whether anything may move as a build sees the project, every project reference its own: the
    /// analyzer's question, cheap enough to ask at each caret — no other project is compiled. The
    /// conversion asks again with the solution in view, and may find less.
    /// </summary>
    public static async Task<bool> MayConvertAsync(Document document, CancellationToken cancellationToken) =>
        await PrepareAsync(document, cancellationToken).ConfigureAwait(false) is (var root, var model, var types) &&
        ProgramConversionAnalyzer.Analyze(
            model,
            root,
            types,
            ProgramConversionAnalyzer.ProjectReferences(model.Compilation, document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions),
            cancellationToken) is not null;

    /// <summary>The document, when it has a program's shape and the project references FusionModules rather than builds it.</summary>
    private static async Task<(CompilationUnitSyntax Root, SemanticModel Model, ProgramConversion.Types Types)?> PrepareAsync(
        Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root ||
            !ProgramConversion.MightApply(root) ||
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model ||
            ModuleFacts.GetModuleBaseType(model.Compilation) is not { } moduleBase ||
            SymbolEqualityComparer.Default.Equals(moduleBase.ContainingAssembly, model.Compilation.Assembly) ||
            ProgramConversion.Types.For(model.Compilation) is not { } types)
        {
            return null;
        }

        return (root, model, types);
    }

    /// <summary>
    /// An own method's declarations in its project's compilation, found by its documentation ID: in
    /// a workspace a referenced project's symbols may not lead to their source by themselves.
    /// </summary>
    private static IEnumerable<(SyntaxNode Declaration, Compilation Compilation)> Sources(
        IMethodSymbol method, Dictionary<string, Compilation> compilations, CancellationToken cancellationToken)
    {
        if (!compilations.TryGetValue(method.ContainingAssembly.Identity.Name, out var compilation) ||
            DocumentationCommentId.CreateDeclarationId(method) is not { } id)
        {
            yield break;
        }

        foreach (var symbol in DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).OfType<IMethodSymbol>())
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                if (compilation.SyntaxTrees.Contains(reference.SyntaxTree))
                {
                    yield return (reference.GetSyntax(cancellationToken), compilation);
                }
            }
        }
    }

    /// <summary>The conversion; the solution unchanged when nothing moves.</summary>
    public static async Task<Solution> ConvertAsync(Document document, CancellationToken cancellationToken)
    {
        var converted = await ConvertCoreAsync(document, cancellationToken).ConfigureAwait(false);
        return converted == document.Project.Solution
            ? converted
            : await TidyAsync(converted, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The program after the move, tidied: the usings only the moved code needed go, and runs of
    /// blank lines where statements left close up — unless the program had such runs already, as
    /// its author's choice.
    /// </summary>
    private static async Task<Solution> TidyAsync(Solution solution, Document original, CancellationToken cancellationToken)
    {
        if (solution.GetDocument(original.Id) is not { } changed ||
            await changed.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
        {
            return solution;
        }

        var before = await UnusedUsingsAsync(original, cancellationToken).ConfigureAwait(false);
        var after = await UnusedUsingsAsync(changed, cancellationToken).ConfigureAwait(false);
        var unused = root.Usings
            .Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.None) && after.Contains(Key(directive)) && !before.Contains(Key(directive)))
            .ToList();
        if (unused.Count > 0)
        {
            root = root.RemoveNodes(unused, SyntaxRemoveOptions.KeepLeadingTrivia)!;
        }

        var originalText = (await original.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        var text = root.ToFullString();
        if (!HasBlankLineRuns(originalText))
        {
            var lineBreak = ModuleEditing.LineBreakOf(root);
            text = CollapseBlankLines(text.Replace("\r\n", "\n")).Replace("\n", lineBreak);
        }

        var encoding = (await changed.GetTextAsync(cancellationToken).ConfigureAwait(false)).Encoding;
        return solution.WithDocumentText(original.Id, SourceText.From(text, encoding));

        static string Key(UsingDirectiveSyntax directive) => directive.WithoutTrivia().ToString();
    }

    /// <summary>
    /// The usings of a document the compiler finds unnecessary (CS8019). It reports them only where
    /// documentation comments are parsed — a <c>cref</c> may be what uses one — so the document is
    /// read that way for the question.
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
            if (diagnostic.Id == "CS8019" &&
                root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>() is { } directive)
            {
                unused.Add(directive.WithoutTrivia().ToString());
            }
        }

        return unused;
    }

    private static async Task<Solution> ConvertCoreAsync(Document document, CancellationToken cancellationToken)
    {
        var solution = document.Project.Solution;
        if (await PlanAsync(document, cancellationToken).ConfigureAwait(false) is not { } plan ||
            await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root ||
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return solution;
        }

        var project = document.Project;
        var existing = await ModuleEditing.FindAsync(project, cancellationToken).ConfigureAwait(false);

        // A module class in this very document would be edited twice by one change.
        if (existing is not null && existing.Document.Id == document.Id)
        {
            return solution;
        }

        var lineBreak = ModuleEditing.LineBreakOf(root);
        var namespaces = Namespaces(plan, model, cancellationToken);

        // The names the module's code uses: an existing override's parameter, or the usual one, as
        // long as nothing in the moved code has that name already.
        MethodDeclarationSyntax? configureServices = null;
        MethodDeclarationSyntax? configure = null;
        SemanticModel? moduleModel = null;
        if (existing is not null)
        {
            moduleModel = await existing.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (moduleModel is null)
            {
                return solution;
            }

            configureServices = ModuleEditing.FindOverride(existing.Declaration, moduleModel, "ConfigureServices", ModuleFacts.HostApplicationBuilderMetadataName, cancellationToken);
            configure = ModuleEditing.FindOverride(existing.Declaration, moduleModel, "Configure", ModuleFacts.ApplicationBuilderMetadataName, cancellationToken);
        }

        var builder = PickName(plan.Registrations, configureServices?.ParameterList.Parameters[0].Identifier.Text, "builder", "hostBuilder", "applicationBuilder");
        var endpoints = PickName(plan.Mappings, null, "endpoints", "routes", "endpointRoutes");
        var app = PickName(plan.Mappings, configure?.ParameterList.Parameters[0].Identifier.Text, "app", "application");
        if (builder is null || endpoints is null || app is null)
        {
            return solution;
        }

        var registrations = Text(plan.Registrations, builder, endpoints);
        var mappings = plan.Mappings.Count == 0
            ? string.Empty
            : $"{app}.UseEndpoints({endpoints} =>\n{{\n{ModuleEditing.Indent(Text(plan.Mappings, builder, endpoints), "    ", "\n")}\n}});";

        // Out of the program first: the module's document may be new, and the program's nodes are
        // only valid in the root they came from.
        // UseModuleRouting is an extension of FusionModules: the program needs the namespace unless
        // a global using brings it already.
        var importsFusionModules = !plan.RoutingCall.StartsWith("UseModuleRouting", System.StringComparison.Ordinal) ||
            model.GetImportScopes(root.SpanStart).SelectMany(scope => scope.Imports)
                .Any(import => import.NamespaceOrType is INamespaceSymbol { Name: "FusionModules", ContainingNamespace.IsGlobalNamespace: true });
        solution = solution.WithDocumentSyntaxRoot(document.Id, RemoveFromProgram(root, plan, lineBreak, importsFusionModules));

        if (existing is not null)
        {
            if (registrations.Length > 0)
            {
                namespaces.Add("Microsoft.Extensions.Hosting");
            }

            if (mappings.Length > 0)
            {
                namespaces.Add("Microsoft.AspNetCore.Builder");
            }

            var declaration = existing.Declaration;
            var marked = Mark(declaration, configureServices, configure, out var servicesMark, out var configureMark);
            var moduleLineBreak = ModuleEditing.LineBreakOf(await existing.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? root);

            if (registrations.Length > 0)
            {
                marked = marked.GetAnnotatedNodes(servicesMark).OfType<MethodDeclarationSyntax>().SingleOrDefault() is { } method
                    ? ModuleEditing.AppendToOverride(marked, method, registrations, moduleLineBreak)
                    : ModuleEditing.AddOverride(marked, $"protected override void ConfigureServices(IHostApplicationBuilder {builder})", registrations, moduleLineBreak);
            }

            if (mappings.Length > 0)
            {
                marked = marked.GetAnnotatedNodes(configureMark).OfType<MethodDeclarationSyntax>().SingleOrDefault() is { } method
                    ? ModuleEditing.AppendToOverride(marked, method, mappings, moduleLineBreak)
                    : ModuleEditing.AddOverride(marked, $"protected override void Configure(IApplicationBuilder {app})", mappings, moduleLineBreak);
            }

            var moduleRoot = (CompilationUnitSyntax)(await existing.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            var updated = ModuleEditing.EnsureUsings(
                moduleRoot.ReplaceNode(declaration, marked),
                moduleModel!,
                declaration.SpanStart,
                namespaces.Where(ns => !Encloses(ns, existing.Symbol.ContainingNamespace)));
            return solution.WithDocumentSyntaxRoot(existing.Document.Id, updated);
        }

        var encoding = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).Encoding;
        return await CreateModuleAsync(solution, project.Id, registrations, mappings, builder, app, namespaces, lineBreak, encoding, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The project's own code, as far as the solution shows it: the compilation's own assembly, and
    /// the projects it references, directly or not, that no other program references.
    /// </summary>
    /// <remarks>
    /// Only web programs count against it: a worker (<c>Host.CreateApplicationBuilder</c>) cannot
    /// become a module, and one that shares the service's Application project is the same service's
    /// other half — its code goes into the service's module when the time comes, not into a library.
    /// </remarks>
    internal static async Task<(System.Func<IAssemblySymbol, bool> IsOwn, List<ProjectId> Projects)> OwnCodeAsync(
        Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var solution = project.Solution;
        var graph = solution.GetProjectDependencyGraph();
        var dependents = new HashSet<ProjectId>(graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id));
        // Any other program in view means the solution is: a reference no web program shares is the
        // service's own. None at all — dotnet format given the one project — and nothing can be told.
        var inView = false;
        var others = new List<Project>();
        foreach (var other in solution.Projects)
        {
            if (other.Id != project.Id && !dependents.Contains(other.Id) && IsProgram(other) && !IsTest(other))
            {
                inView = true;
                if (await IsWebProgramAsync(other, cancellationToken).ConfigureAwait(false))
                {
                    others.Add(other);
                }
            }
        }

        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectId>();
        if (inView)
        {
            var shared = new HashSet<ProjectId>(others.SelectMany(other => graph.GetProjectsThatThisProjectTransitivelyDependsOn(other.Id)));
            foreach (var id in graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id))
            {
                if (!shared.Contains(id) && solution.GetProject(id) is { } referenced)
                {
                    own.Add(referenced.AssemblyName);
                    projects.Add(id);
                }
            }
        }

        var assembly = compilation.Assembly;
        return (candidate => SymbolEqualityComparer.Default.Equals(candidate, assembly) || own.Contains(candidate.Identity.Name), projects);
    }

    private static bool IsProgram(Project project) =>
        project.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;

    /// <summary>
    /// A program that hosts ASP.NET Core: its own code builds a web host. Having
    /// <c>WebApplication</c> in its references is not enough — a worker gets the shared framework
    /// through a gRPC or health-check package as readily as a web API does.
    /// </summary>
    private static async Task<bool> IsWebProgramAsync(Project project, CancellationToken cancellationToken)
    {
        if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation ||
            compilation.GetTypeByMetadataName(ModuleFacts.WebApplicationMetadataName) is null)
        {
            return false;
        }

        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root &&
                root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name =>
                    name.Identifier.Text is "WebApplication" or "WebHost" or "ConfigureWebHostDefaults" or "ConfigureWebHost" or "UseStartup"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A test project: classified as one by the package's targets, or referencing a test framework.
    /// Test projects are programs too, and reference whatever they test.
    /// </summary>
    private static bool IsTest(Project project) =>
        ModuleFacts.GetProjectKind(project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions) == ProjectKind.Test ||
        project.MetadataReferences.OfType<PortableExecutableReference>().Any(reference =>
            System.IO.Path.GetFileName(reference.FilePath ?? string.Empty) is { Length: > 0 } name &&
            TestFrameworks.Any(prefix => name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] TestFrameworks =
    [
        "xunit", "nunit", "MSTest", "TUnit", "Microsoft.VisualStudio.TestPlatform", "Microsoft.Testing.Platform",
    ];

    /// <summary>
    /// A name for the module's parameter that nothing in the moved code uses already, other than
    /// what the rewrite writes: the existing override's when there is one, or the first free one.
    /// </summary>
    private static string? PickName(List<MovedStatement> statements, string? existing, params string[] candidates)
    {
        var used = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            var replaced = statement.Rewrites.Select(rewrite => rewrite.Key.Span).ToList();
            foreach (var token in statement.Node.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && !replaced.Any(span => span.Contains(token.Span)))
                {
                    used.Add(token.Text);
                }
            }
        }

        if (existing is not null)
        {
            return used.Contains(existing) ? null : existing;
        }

        return candidates.FirstOrDefault(candidate => !used.Contains(candidate));
    }

    /// <summary>The statements as module code, comments included, outdented, with <c>\n</c> line breaks.</summary>
    private static string Text(List<MovedStatement> statements, string builder, string endpoints)
    {
        var parts = statements.Select(statement =>
        {
            var rewritten = statement.Node.ReplaceNodes(
                statement.Rewrites.Select(rewrite => rewrite.Key),
                (original, _) => SyntaxFactory.ParseExpression(Replacement(statement.Rewrites.First(rewrite => rewrite.Key == original).Value, builder, endpoints))
                    .WithTriviaFrom(original));

            // A call cut out of a chain: builder.Services.Call(…), on one line, where the chain was.
            if (statement.IsLink && rewritten is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } link)
            {
                var indentation = statement.Node.FirstAncestorOrSelf<StatementSyntax>() is { } enclosing ? ModuleEditing.IndentationOf(enclosing) : string.Empty;
                return indentation + link.WithExpression(access
                        .WithExpression(access.Expression.WithoutTrivia())
                        .WithOperatorToken(access.OperatorToken.WithoutTrivia()))
                    .WithoutTrivia()
                    .ToString() + ";\n";
            }

            // The expression body of a UseEndpoints lambda becomes a statement of its own.
            return statement.Node is ExpressionSyntax
                ? "\n" + rewritten.ToString() + ";\n"
                : rewritten.ToFullString();
        });

        return CollapseBlankLines(BuilderRewriting.Outdent(string.Concat(parts)).Replace("\r\n", "\n")).Trim('\n');
    }

    /// <summary>Runs of blank lines as one: statements brought together from different groups bring their spacing with them.</summary>
    internal static string CollapseBlankLines(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\n([ \t]*\n){2,}", "\n\n");

    private static bool HasBlankLineRuns(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text.Replace("\r\n", "\n"), @"\n([ \t]*\n){2,}");

    private static string Replacement(ModuleRewrite rewrite, string builder, string endpoints) => rewrite switch
    {
        ModuleRewrite.Builder => builder,
        ModuleRewrite.BuilderServices => $"{builder}.Services",
        ModuleRewrite.BuilderConfiguration => $"{builder}.Configuration",
        ModuleRewrite.BuilderEnvironment => $"{builder}.Environment",
        ModuleRewrite.WebEnvironment => $"((Microsoft.AspNetCore.Hosting.IWebHostEnvironment){builder}.Environment)",
        _ => endpoints,
    };

    /// <summary>
    /// The program without what moved, and with <c>UseRouting</c> after the application is built
    /// when its endpoints moved and it had none: routing <c>WebApplication</c> adds by itself only
    /// while it has endpoints of its own, at the same place.
    /// </summary>
    private static SyntaxNode RemoveFromProgram(CompilationUnitSyntax root, ConversionPlan plan, string lineBreak, bool importsFusionModules)
    {
        var anchor = plan.RoutingAfter is { } after ? (after.Parent as GlobalStatementSyntax ?? (SyntaxNode)after) : null;
        var spacers = Spacers(plan.Removed);
        var tracked = root.TrackNodes(plan.Removed.Concat(spacers).Concat(plan.Kept.Keys).Concat(anchor is null ? [] : [anchor]));

        // A chain cut in two keeps the calls that stayed, as they were written.
        tracked = tracked.ReplaceNodes(
            plan.Kept.Keys.Select(chain => tracked.GetCurrentNode(chain)).OfType<ExpressionSyntax>(),
            (original, _) => plan.Kept.First(pair => tracked.GetCurrentNode(pair.Key) == original).Value.WithTriviaFrom(original));

        if (anchor is not null && tracked.GetCurrentNode(anchor) is { } current)
        {
            var statement = SyntaxFactory.ParseStatement($"{ModuleEditing.IndentationOf(plan.RoutingAfter!)}{plan.ApplicationName}.{plan.RoutingCall};{lineBreak}");
            tracked = tracked.InsertNodesAfter(current, [anchor is GlobalStatementSyntax ? SyntaxFactory.GlobalStatement(statement) : statement]);
        }

        // A blank line that set off a group of statements stays, on the statement that now follows.
        tracked = tracked.ReplaceNodes(
            spacers.Select(node => tracked.GetCurrentNode(node)).OfType<SyntaxNode>(),
            (_, node) => node.WithLeadingTrivia(node.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(lineBreak))));

        var result = (CompilationUnitSyntax)tracked.RemoveNodes(plan.Removed.Select(node => tracked.GetCurrentNode(node)).OfType<SyntaxNode>(), SyntaxRemoveOptions.KeepNoTrivia)!;
        if (!importsFusionModules)
        {
            var directive = SyntaxFactory.ParseCompilationUnit("using FusionModules;" + lineBreak).Usings[0];
            var index = InsertionIndex(result.Usings);
            if (result.Usings.Count == 0)
            {
                // The file's first using: set off from the code by a blank line.
                result = result.WithUsings(result.Usings.Add(directive.WithTrailingTrivia(SyntaxFactory.EndOfLine(lineBreak), SyntaxFactory.EndOfLine(lineBreak))));
            }
            else if (index == 0)
            {
                // Before the first using, inside whatever opens the list: a #region, a file header.
                var first = result.Usings[0];
                result = result.WithUsings(result.Usings
                    .Replace(first, first.WithLeadingTrivia())
                    .Insert(0, directive.WithLeadingTrivia(first.GetLeadingTrivia())));
            }
            else
            {
                result = result.WithUsings(result.Usings.Insert(index, directive));
            }
        }

        return result;
    }

    /// <summary>Where <c>using FusionModules;</c> goes: after the System ones, in order among the rest.</summary>
    private static int InsertionIndex(SyntaxList<UsingDirectiveSyntax> usings)
    {
        var index = 0;
        while (index < usings.Count &&
               usings[index].Name?.ToString() is { } name &&
               (name == "System" || name.StartsWith("System.", System.StringComparison.Ordinal) || string.CompareOrdinal(name, "FusionModules") < 0))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// The statements that follow a run of removed ones which began with a blank line, when they
    /// do not begin with one themselves: without it, two groups of the program would run together.
    /// </summary>
    private static List<SyntaxNode> Spacers(List<SyntaxNode> removedNodes)
    {
        var removed = new HashSet<SyntaxNode>(removedNodes);
        var spacers = new List<SyntaxNode>();
        foreach (var parent in removedNodes.Select(node => node.Parent).OfType<SyntaxNode>().Distinct())
        {
            var siblings = parent.ChildNodes().ToList();
            for (var i = 1; i < siblings.Count; i++)
            {
                if (!removed.Contains(siblings[i]) || removed.Contains(siblings[i - 1]) || !StartsWithBlankLine(siblings[i]))
                {
                    continue;
                }

                var next = i;
                while (next < siblings.Count && removed.Contains(siblings[next]))
                {
                    next++;
                }

                if (next < siblings.Count && siblings[next] is StatementSyntax or GlobalStatementSyntax && !StartsWithBlankLine(siblings[next]))
                {
                    spacers.Add(siblings[next]);
                }
            }
        }

        return spacers;
    }

    private static bool StartsWithBlankLine(SyntaxNode node) =>
        node.GetLeadingTrivia().FirstOrDefault(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia)).IsKind(SyntaxKind.EndOfLineTrivia);

    private static ClassDeclarationSyntax Mark(
        ClassDeclarationSyntax declaration,
        MethodDeclarationSyntax? configureServices,
        MethodDeclarationSyntax? configure,
        out SyntaxAnnotation servicesMark,
        out SyntaxAnnotation configureMark)
    {
        var services = servicesMark = new SyntaxAnnotation();
        var pipeline = configureMark = new SyntaxAnnotation();
        return declaration.ReplaceNodes(
            new[] { configureServices, configure }.Where(method => method is not null).Cast<SyntaxNode>(),
            (original, current) => current.WithAdditionalAnnotations(original == configureServices ? services : pipeline));
    }

    /// <summary>
    /// A new <c>Module.cs</c> with the module class, its hosting startup attribute and the usings it
    /// needs, in the program's encoding.
    /// </summary>
    private static async Task<Solution> CreateModuleAsync(
        Solution solution,
        ProjectId projectId,
        string registrations,
        string mappings,
        string builder,
        string app,
        HashSet<string> namespaces,
        string lineBreak,
        System.Text.Encoding? encoding,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId)!;
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return solution;
        }

        var ns = RootNamespace(project);
        var className = ClassName(project, compilation, ns);

        // A Module.cs the project file still lists but that holds nothing — left by an earlier run
        // whose file was deleted — is the module's file, not a reason to write Module2.cs.
        var directory = project.FilePath is { } projectPath ? System.IO.Path.GetDirectoryName(projectPath) : null;
        Document? reusable = null;
        foreach (var candidate in project.Documents)
        {
            if (directory is not null &&
                candidate.FilePath is { } candidatePath &&
                string.Equals(candidatePath, System.IO.Path.Combine(directory, className + ".cs"), System.StringComparison.OrdinalIgnoreCase) &&
                (await candidate.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString().Trim().Length == 0)
            {
                reusable = candidate;
                break;
            }
        }

        var fileName = reusable is not null ? className + ".cs" : FileName(project, className);
        var fileScoped = project.ParseOptions is CSharpParseOptions options &&
            options.LanguageVersion.MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp10;

        namespaces.Add("FusionModules");
        namespaces.Add("Microsoft.AspNetCore.Hosting");
        if (registrations.Length > 0)
        {
            namespaces.Add("Microsoft.Extensions.Hosting");
        }

        if (mappings.Length > 0)
        {
            namespaces.Add("Microsoft.AspNetCore.Builder");
        }

        var members = new List<string>();
        if (registrations.Length > 0)
        {
            members.Add(Method($"protected override void ConfigureServices(IHostApplicationBuilder {builder})", registrations));
        }

        if (mappings.Length > 0)
        {
            members.Add(Method($"protected override void Configure(IApplicationBuilder {app})", mappings));
        }

        string Render(IEnumerable<string> usings)
        {
            var lines = new List<string>();
            lines.AddRange(usings.Select(name => $"using {name};"));
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[assembly: HostingStartup(typeof({(ns.Length > 0 ? ns + "." : string.Empty)}{className}))]");
            lines.Add(string.Empty);

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
                indent = "    ";
            }

            lines.Add($"{indent}internal sealed class {className} : ModuleBase");
            lines.Add($"{indent}{{");
            lines.Add(ModuleEditing.Indent(string.Join("\n\n", members), indent + "    ", "\n"));
            lines.Add($"{indent}}}");
            if (ns.Length > 0 && !fileScoped)
            {
                lines.Add("}");
            }

            return (string.Join("\n", lines) + "\n").Replace("\n", lineBreak);
        }

        // Added once without usings to learn which are in scope already — global and implicit ones —
        // and then written with only the missing ones.
        var filePath = project.FilePath is { } path ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, fileName) : null;
        var draft = reusable is not null
            ? reusable.WithText(SourceText.From(Render([]), encoding))
            : project.AddDocument(fileName, SourceText.From(Render([]), encoding), filePath: filePath);
        var model = await draft.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var draftRoot = await draft.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var position = draftRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.SpanStart ?? 0;
        var imported = model is null
            ? new HashSet<string>(System.StringComparer.Ordinal)
            : new HashSet<string>(
                model.GetImportScopes(position).SelectMany(scope => scope.Imports).Select(import => import.NamespaceOrType)
                    .OfType<INamespaceSymbol>().Select(symbol => symbol.ToDisplayString()),
                System.StringComparer.Ordinal);
        var missing = namespaces
            .Where(name => name.Length > 0 && !imported.Contains(name) && !Encloses(name, ns))
            .Distinct()
            .OrderBy(name => name == "System" || name.StartsWith("System.", System.StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(name => name, System.StringComparer.Ordinal);

        return draft.WithText(SourceText.From(Render(missing), encoding)).Project.Solution;

        static string Method(string signature, string statements) =>
            signature + "\n{\n" + ModuleEditing.Indent(statements, "    ", "\n") + "\n}";
    }

    /// <summary>Whether a <c>using</c> of <paramref name="name"/> is redundant inside <paramref name="ns"/>: it is that namespace or encloses it.</summary>
    private static bool Encloses(string name, string ns) =>
        name == ns || ns.StartsWith(name + ".", System.StringComparison.Ordinal);

    private static bool Encloses(string name, INamespaceSymbol? ns) =>
        ns is { IsGlobalNamespace: false } && Encloses(name, ns.ToDisplayString());

    /// <summary>The project's root namespace, as an identifier path.</summary>
    private static string RootNamespace(Project project)
    {
        var name = project.DefaultNamespace is { Length: > 0 } ns ? ns : project.AssemblyName;
        var parts = name.Split('.').Select(Identifier).Where(part => part.Length > 0);
        return string.Join(".", parts);
    }

    private static string Identifier(string part)
    {
        var cleaned = new string(part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return cleaned.Length > 0 && char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    /// <summary><c>Module</c>, or — when the namespace has one already — the project's name and <c>Module</c>.</summary>
    private static string ClassName(Project project, Compilation compilation, string ns)
    {
        string Qualified(string name) => ns.Length > 0 ? ns + "." + name : name;

        if (compilation.GetTypeByMetadataName(Qualified(ModuleClassName)) is null)
        {
            return ModuleClassName;
        }

        var stem = string.Concat(project.AssemblyName.Split('.', '-', ' ').Select(Identifier).Select(part =>
            part.Length > 0 ? char.ToUpperInvariant(part[0]) + part.Substring(1) : part)) + ModuleClassName;
        var name = stem;
        for (var i = 2; compilation.GetTypeByMetadataName(Qualified(name)) is not null; i++)
        {
            name = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static string FileName(Project project, string className)
    {
        var directory = project.FilePath is { } path ? System.IO.Path.GetDirectoryName(path) : null;
        bool Taken(string name) => project.Documents.Any(document =>
            (document.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase) && document.Folders.Count == 0) ||
            (directory is not null && document.FilePath is { } file &&
             string.Equals(file, System.IO.Path.Combine(directory, name), System.StringComparison.OrdinalIgnoreCase)));

        var fileName = $"{className}.cs";
        for (var i = 2; Taken(fileName); i++)
        {
            fileName = $"{className}{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}.cs";
        }

        return fileName;
    }

    /// <summary>
    /// The namespaces the moved code's names come from: types named by their simple name, and the
    /// classes extension methods are declared in — which <c>using</c> brings an extension method
    /// into scope is not visible in the call.
    /// </summary>
    private static HashSet<string> Namespaces(ConversionPlan plan, SemanticModel model, CancellationToken cancellationToken)
    {
        var namespaces = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var statement in plan.Registrations.Concat(plan.Mappings))
        {
            // What a rewrite replaces is not in the module: the builder, or the chain a call was cut from.
            var replaced = statement.Rewrites.Select(rewrite => rewrite.Key.Span).ToList();
            foreach (var name in statement.Node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                if (replaced.Any(span => span.Contains(name.Span)))
                {
                    continue;
                }

                if (name is IdentifierNameSyntax { IsVar: true })
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                INamespaceSymbol? ns = symbol switch
                {
                    IMethodSymbol { ReducedFrom: not null } extension => extension.ContainingType.ContainingNamespace,
                    IMethodSymbol { ContainingType.IsExtension: true } member => member.ContainingType.ContainingNamespace,
                    INamedTypeSymbol type when !IsQualified(name) => type.ContainingNamespace,
                    _ => null,
                };

                if (ns is { IsGlobalNamespace: false })
                {
                    namespaces.Add(ns.ToDisplayString());
                }
            }
        }

        return namespaces;
    }

    private static bool IsQualified(SimpleNameSyntax name) =>
        (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name) ||
        (name.Parent is MemberAccessExpressionSyntax access && access.Name == name);
}
