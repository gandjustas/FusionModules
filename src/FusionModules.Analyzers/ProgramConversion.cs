using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionModules.Analyzers;

/// <summary>
/// Which statements of a program's startup code are the project's own and can move into a module
/// of the same project (MOD0015): registrations that name the project's own code, and mappings of
/// its own endpoints. Everything else — the framework's and the shared libraries' registrations,
/// middleware, migrations, <c>Run()</c> — stays where it is.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes are read: top-level statements, a <c>Main</c> method, both with a
/// <c>WebApplicationBuilder</c> local, however made; and a classic <c>Startup</c> class's
/// <c>ConfigureServices(IServiceCollection)</c> and the <c>UseEndpoints</c> at the end of its
/// <c>Configure</c>. Locals that name a part of the builder (<c>var configuration =
/// builder.Configuration</c>) are that part, in the module as in the program.
/// </para>
/// <para>
/// An own method moves only when everything it registers is the project's: its body, and those of
/// the own methods it calls, are read as the program is. A chain whose every call takes and returns
/// the service collection gives up its own calls and keeps the rest. A registration of someone
/// else's contract — in the program or in an own method — moves only when nothing before it stays,
/// since the last registration wins.
/// </para>
/// <para>
/// A registration is a statement that starts from the builder (an extension of
/// <c>IHostApplicationBuilder</c>), from <c>builder.Services</c>, or from <c>Startup</c>'s service
/// collection, and whose first call — and every later one on the builder or the collection — is the
/// project's own: declared in its own code, or naming its own types — as type arguments, in
/// <c>typeof</c>, in a constructed object or a lambda's body. A call on what an earlier one returned
/// (an own <c>OptionsBuilder</c>, an own client's <c>IHttpClientBuilder</c>) configures that. A chain
/// with someone else's call on the collection stays even if it names own types; so does a call that
/// shapes the whole application (<c>AddAuthentication</c>, <c>AddControllers</c>…) and one whose
/// meaning depends on running after the host's registrations (<c>Replace</c>, <c>RemoveAll</c>,
/// <c>PostConfigure</c>, <c>Decorate</c>): the module runs first.
/// </para>
/// <para>
/// A mapping starts from the application (or <c>UseEndpoints</c>' route builder) with an extension
/// of <c>IEndpointRouteBuilder</c>, and maps something of the project's: a handler written in its
/// code, an own type such as a hub or a gRPC service, or an own <c>MapXxx</c> extension.
/// </para>
/// <para>
/// What "own" means is the caller's: the analyzer knows only the compilation, the code fix also
/// knows which project references no other program in the solution shares.
/// </para>
/// <para>
/// A statement moves only if it keeps its meaning in the module: it may use the builder's
/// <c>Services</c>, <c>Configuration</c> and <c>Environment</c> (or, in a <c>Startup</c>, its
/// configuration and environment members), and locals that move with it; nothing else of the
/// program. A local moves when only moving statements use it.
/// </para>
/// </remarks>
internal static class ProgramConversion
{
    private const string WebApplicationBuilderMetadataName = "Microsoft.AspNetCore.Builder.WebApplicationBuilder";
    private const string EndpointRouteBuilderMetadataName = "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";
    private const string ServiceCollectionMetadataName = "Microsoft.Extensions.DependencyInjection.IServiceCollection";
    private const string ConfigurationMetadataName = "Microsoft.Extensions.Configuration.IConfiguration";
    private const string HostEnvironmentMetadataName = "Microsoft.Extensions.Hosting.IHostEnvironment";

    /// <summary>
    /// Framework calls that configure the whole application rather than a part of it. A module of
    /// the monolith must not own them, whatever types their options name, so they stay.
    /// </summary>
    internal static readonly ImmutableHashSet<string> ApplicationWide =
    [
        "AddAntiforgery", "AddApiVersioning", "AddAuthentication", "AddAuthorization", "AddAuthorizationBuilder",
        "AddControllers", "AddControllersWithViews", "AddCors", "AddDataProtection",
        "AddEndpointsApiExplorer", "AddExceptionHandler", "AddGrpc", "AddGrpcReflection", "AddHealthChecks",
        "AddHsts", "AddHttpContextAccessor", "AddHttpLogging", "AddHttpsRedirection", "AddLocalization",
        "AddLogging", "AddMvc", "AddMvcCore", "AddOpenApi", "AddOpenTelemetry", "AddOutputCache",
        "AddProblemDetails", "AddRateLimiter", "AddRazorComponents", "AddRazorPages", "AddRequestTimeouts",
        "AddResponseCaching", "AddResponseCompression", "AddRouting", "AddServerSideBlazor",
        "AddSession", "AddSignalR", "AddSwaggerGen", "AddW3CLogging", "ConfigureHttpJsonOptions",
        "ConfigureOpenTelemetry", "AddIdentity", "AddIdentityCore", "AddIdentityApiEndpoints",
        "AddFeatureManagement",
        // One of each per process: a message bus with its transport, a job scheduler.
        "AddMassTransit", "AddQuartz", "AddQuartzHostedService", "AddHangfire", "AddHangfireServer",
    ];

    /// <summary>
    /// Mappings that belong to the whole application whatever they name: the Blazor shell with its
    /// <c>/_blazor</c> endpoints, a catch-all fallback, every controller or page at once, the
    /// OpenAPI document, health checks, static assets. Two modules each mapping one would collide.
    /// </summary>
    internal static readonly ImmutableHashSet<string> ApplicationWideMappings =
    [
        "MapRazorComponents", "MapBlazorHub", "MapFallback", "MapFallbackToFile", "MapFallbackToPage",
        "MapFallbackToController", "MapFallbackToAreaPage", "MapFallbackToAreaController", "MapStaticAssets",
        "MapOpenApi", "MapSwagger", "MapHealthChecks", "MapRazorPages", "MapControllers", "MapControllerRoute",
        "MapDefaultControllerRoute", "MapAreaControllerRoute", "MapGrpcReflectionService", "MapIdentityApi",
        "MapDefaultEndpoints", "MapPrometheusScrapingEndpoint",
    ];

    /// <summary>
    /// Calls that map endpoints whenever they run: the application-wide mappings, the minimal API
    /// verbs, and the libraries' own. Not <c>MapDefaultEndpoints</c>: eShop's maps <c>/health</c> in
    /// Development only.
    /// </summary>
    private static readonly ImmutableHashSet<string> FrameworkMappings = ApplicationWideMappings
        .Remove("MapDefaultEndpoints")
        .Union(["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapHub", "MapGrpcService", "MapReverseProxy", "MapCarter"]);

    /// <summary>
    /// Whether a call configures the whole application: one of <see cref="ApplicationWide"/>, or
    /// named the way a solution's service defaults are — <c>AddServiceDefaults</c>,
    /// <c>AddBasicServiceDefaults</c>, <c>AddDefaultAuthentication</c>, <c>AddDefaultIdentity</c>.
    /// </summary>
    internal static bool IsApplicationWide(string name) =>
        ApplicationWide.Contains(name) ||
        name.EndsWith("ServiceDefaults", StringComparison.Ordinal) ||
        (name.StartsWith("AddDefault", StringComparison.Ordinal) && name.Length > "AddDefault".Length && char.IsUpper(name["AddDefault".Length]));

    /// <summary>
    /// Prefixes of calls whose meaning depends on what was registered before them. The module's
    /// registrations run before the program's, so these would no longer see what they act on.
    /// <c>PostConfigure</c> and <c>Decorate</c> are read apart: on the project's own options or
    /// service they act on what moves with them.
    /// </summary>
    private static readonly string[] OrderSensitivePrefixes = ["Replace", "Remove"];

    /// <summary>
    /// Receivers of calls that configure the host as a whole — logging, metrics, configuration
    /// sources, the host and web host builders. An own method that makes such a call is not the
    /// project's alone, whatever else it registers.
    /// </summary>
    private static readonly string[] ApplicationWideReceivers =
    [
        "Microsoft.Extensions.Logging.ILoggingBuilder",
        "Microsoft.Extensions.Diagnostics.Metrics.IMetricsBuilder",
        "Microsoft.Extensions.Configuration.IConfigurationBuilder",
        "Microsoft.Extensions.Configuration.IConfigurationManager",
        "Microsoft.Extensions.Configuration.ConfigurationManager",
        "Microsoft.AspNetCore.Builder.ConfigureHostBuilder",
        "Microsoft.AspNetCore.Builder.ConfigureWebHostBuilder",
        "Microsoft.Extensions.Hosting.IHostBuilder",
        "Microsoft.AspNetCore.Hosting.IWebHostBuilder",
    ];

    /// <summary>A cheap syntactic check that a document has something this could read.</summary>
    public static bool MightApply(CompilationUnitSyntax root) =>
        root.Members.Any(member => member is GlobalStatementSyntax) ||
        Methods(root).Any(method => method.Identifier.Text is "Main" or "ConfigureServices" or "Configure");

    /// <summary>
    /// What moves out of this document, or null when nothing does.
    /// </summary>
    /// <param name="model">The document's semantic model.</param>
    /// <param name="root">The document's root.</param>
    /// <param name="types">The framework's types, from <see cref="Types.For"/> on the document's compilation.</param>
    /// <param name="isOwn">Whether an assembly is the project's own code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="sources">
    /// Where an own method's source is, when its symbol does not say: the code fix sees the
    /// solution, where a referenced project's method may come from its own compilation.
    /// </param>
    public static ConversionPlan? Analyze(
        SemanticModel model,
        CompilationUnitSyntax root,
        Types types,
        Func<IAssemblySymbol, bool> isOwn,
        CancellationToken cancellationToken,
        Func<IMethodSymbol, IEnumerable<(SyntaxNode Declaration, Compilation Compilation)>>? sources = null)
    {
        var analysis = new Analysis(model, root, types, isOwn, cancellationToken) { Sources = sources };

        var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            analysis.Program(
                globals.Select(global => (global.Statement, (SyntaxNode)global)),
                model.GetEnclosingSymbol(globals[0].Statement.SpanStart, cancellationToken)?.ContainingType);
        }

        foreach (var method in Methods(root))
        {
            if (method.Body is null || model.GetDeclaredSymbol(method, cancellationToken) is not { } symbol)
            {
                continue;
            }

            switch (method.Identifier.Text)
            {
                case "Main" when symbol.IsStatic:
                    analysis.Program(method.Body.Statements.Select(statement => (statement, (SyntaxNode)statement)), symbol.ContainingType);
                    break;
                case "ConfigureServices" when IsStartup(symbol.ContainingType, types) &&
                                             !symbol.IsStatic &&
                                             symbol.Parameters.Length == 1 &&
                                             SymbolEqualityComparer.Default.Equals(symbol.Parameters[0].Type, types.ServiceCollection):
                    analysis.Services(method.Body, symbol);
                    break;
                case "Configure" when IsStartup(symbol.ContainingType, types) &&
                                     !symbol.IsStatic &&
                                     symbol.Parameters.Length > 0 &&
                                     SymbolEqualityComparer.Default.Equals(symbol.Parameters[0].Type, types.ApplicationBuilder):
                    analysis.Endpoints(method.Body, symbol);
                    break;
            }
        }

        return analysis.Plan.Registrations.Count + analysis.Plan.Mappings.Count == 0 ? null : analysis.Plan;
    }

    /// <summary>
    /// Whether the call would bind on <paramref name="target"/>: written against it, or generic over a
    /// receiver constrained to it — Aspire's <c>AddServiceDefaults&lt;TBuilder&gt;</c> is the common case.
    /// </summary>
    internal static bool Accepts(IMethodSymbol method, INamedTypeSymbol? target) =>
        target is not null &&
        Receiver(method) switch
        {
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(constraint => SymbolEqualityComparer.Default.Equals(constraint, target)),
            var type => SymbolEqualityComparer.Default.Equals(type, target),
        };

    /// <remarks>
    /// A C# 14 extension block's member — Asp.Versioning's <c>NewVersionedApi</c> is one — is
    /// declared in the block's marker type; what it extends is the block's parameter.
    /// </remarks>
    private static ITypeSymbol? Receiver(IMethodSymbol method) =>
        method.ReducedFrom is { } extension ? extension.Parameters.FirstOrDefault()?.Type :
        method.IsExtensionMethod ? method.Parameters.FirstOrDefault()?.Type :
        method.ContainingType is { IsExtension: true, ExtensionParameter: { } parameter } ? parameter.Type :
        method.ContainingType;

    private static IEnumerable<MethodDeclarationSyntax> Methods(CompilationUnitSyntax root) =>
        root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
            .OfType<MethodDeclarationSyntax>();

    /// <summary>
    /// A classic startup class: what <c>UseStartup&lt;T&gt;</c> takes, by the convention ASP.NET Core
    /// itself follows — an instance <c>Configure(IApplicationBuilder, …)</c>. A module, a hosting
    /// startup or a startup filter has a <c>Configure</c> too and is not one.
    /// </summary>
    private static bool IsStartup(INamedTypeSymbol type, Types types) =>
        type.TypeKind == TypeKind.Class &&
        !type.IsStatic &&
        !type.IsAbstract &&
        (types.ModuleBase is null || !ModuleFacts.InheritsFrom(type, types.ModuleBase)) &&
        !type.AllInterfaces.Any(@interface =>
            SymbolEqualityComparer.Default.Equals(@interface, types.HostingStartup) ||
            SymbolEqualityComparer.Default.Equals(@interface, types.StartupFilter)) &&
        type.GetMembers("Configure").OfType<IMethodSymbol>().Any(method =>
            !method.IsStatic &&
            method.Parameters.Length > 0 &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, types.ApplicationBuilder));

    /// <summary>A statement of a body, the node that goes when it moves, and what is read of it once.</summary>
    private sealed class Entry(StatementSyntax statement, SyntaxNode removable)
    {
        public StatementSyntax Statement { get; } = statement;

        public SyntaxNode Removable { get; } = removable;

        /// <summary>The local it declares, when it declares one alone.</summary>
        public ILocalSymbol? Declares { get; set; }

        /// <summary>When it is a call chain: the name it starts from, and its links.</summary>
        public (IdentifierNameSyntax Start, List<Call> Calls)? Chain { get; set; }

        /// <summary>What <see cref="Chain"/> starts from.</summary>
        public ISymbol? StartSymbol { get; set; }

        /// <summary>What it uses of the builder, the service collection, their aliases and the locals that could move.</summary>
        public HashSet<ISymbol> Uses { get; } = new(SymbolEqualityComparer.Default);
    }

    /// <summary>Whose code a call registers.</summary>
    private enum Ownership
    {
        /// <summary>The project's own.</summary>
        Own,

        /// <summary>The project's own, under someone else's contract: see <see cref="OrderRule.NothingBeforeStays"/>.</summary>
        ForeignContract,

        /// <summary>Not the project's alone.</summary>
        NotOwn,
    }

    /// <summary>What a registration needs of the order it runs in, the module's being first.</summary>
    private enum OrderRule
    {
        /// <summary>Nothing: what it registers is the project's, and nothing else registers it.</summary>
        None,

        /// <summary>
        /// It registers a contract of someone else's, where the last registration wins: it may move
        /// only if nothing before it stays, or a registration of the host's that came first would win.
        /// </summary>
        NothingBeforeStays,

        /// <summary>It decorates a service: it may move only with the registration it decorates.</summary>
        AfterItsRegistration,
    }

    /// <summary>A statement that can move, with what it needs.</summary>
    private sealed class Candidate(
        Entry? entry, SyntaxNode node, bool isMapping, ILocalSymbol? declares, ImmutableArray<KeyValuePair<SyntaxNode, ModuleRewrite>> rewrites, HashSet<ILocalSymbol> locals)
    {
        public Entry? Entry { get; } = entry;

        public SyntaxNode Node { get; } = node;

        public bool IsMapping { get; } = isMapping;

        /// <summary>The local this statement declares, when it is a local declaration.</summary>
        public ILocalSymbol? Declares { get; } = declares;

        public ImmutableArray<KeyValuePair<SyntaxNode, ModuleRewrite>> Rewrites { get; } = rewrites;

        /// <summary>The program's locals this statement uses.</summary>
        public HashSet<ILocalSymbol> Locals { get; } = locals;

        public OrderRule Order { get; init; }

        /// <summary>The service a <see cref="OrderRule.AfterItsRegistration"/> candidate decorates.</summary>
        public ITypeSymbol? Decorates { get; init; }

        /// <summary>The service type its first call registers, when it is a plain <c>AddXxx&lt;TService, …&gt;</c>.</summary>
        public ITypeSymbol? Registers { get; init; }

        /// <summary>
        /// For a chain cut in two: the calls that move, each as a statement of its own, while the
        /// program keeps <see cref="Kept"/> in place of the chain.
        /// </summary>
        public ImmutableArray<MovedStatement> Pieces { get; init; } = [];

        /// <summary>For a chain cut in two: what the program keeps in place of <see cref="Chain"/>.</summary>
        public ExpressionSyntax? Kept { get; init; }

        /// <summary>For a chain cut in two: the whole chain, as written.</summary>
        public ExpressionSyntax? Chain { get; init; }
    }

    /// <summary>What a statement may refer to of the code around it.</summary>
    private sealed class Scope
    {
        /// <summary>The program's <c>WebApplicationBuilder</c> local.</summary>
        public ILocalSymbol? Builder { get; set; }

        /// <summary>A startup's <c>IServiceCollection</c> parameter.</summary>
        public IParameterSymbol? Services { get; set; }

        /// <summary>The program's <c>WebApplication</c> local.</summary>
        public ILocalSymbol? App { get; set; }

        /// <summary>The type the body is in: <c>Program</c> or <c>Startup</c>.</summary>
        public INamedTypeSymbol? Enclosing { get; set; }

        /// <summary>The body's locals that could move with a registration, by the statement declaring them.</summary>
        public Dictionary<ILocalSymbol, Entry> Locals { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Locals that are another name for a part of the builder — <c>var configuration = builder.Configuration;</c> —
        /// how each is written in the module, and the statement declaring it.
        /// </summary>
        public Dictionary<ILocalSymbol, (ModuleRewrite Rewrite, Entry Declaration)> Aliases { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>Locals that are route builders made from the application: <c>var api = app.MapGroup("/api");</c>.</summary>
        public HashSet<ILocalSymbol> RouteBuilders { get; } = new(SymbolEqualityComparer.Default);
    }

    private sealed class Analysis(
        SemanticModel model, CompilationUnitSyntax root, Types types, Func<IAssemblySymbol, bool> isOwn, CancellationToken cancellationToken)
    {
        private readonly Dictionary<IMethodSymbol, Ownership> _bodies = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<IMethodSymbol, HashSet<string>> _places = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

        public Func<IMethodSymbol, IEnumerable<(SyntaxNode Declaration, Compilation Compilation)>>? Sources { get; init; }

        private List<int>? _conditionalDirectives;

        public ConversionPlan Plan { get; } = new();

        /// <summary>Top-level statements or a <c>Main</c> method that create a <c>WebApplication</c>.</summary>
        public void Program(IEnumerable<(StatementSyntax Statement, SyntaxNode Removable)> statements, INamedTypeSymbol? enclosing)
        {
            if (types.WebApplication is null || types.WebApplicationBuilder is null)
            {
                return;
            }

            var body = Read(statements);
            Entry? appDeclaration = null;
            var builderIndex = -1;
            var scope = new Scope { Enclosing = enclosing };

            for (var i = 0; i < body.Count; i++)
            {
                if (body[i].Declares is not { } local ||
                    body[i].Statement is not LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables } declaration)
                {
                    continue;
                }

                var value = variables[0].Initializer?.Value;
                if (scope.Builder is null)
                {
                    // Whatever made it: WebApplication.CreateBuilder, or a service template's
                    // factory around it. Every WebApplicationBuilder comes from WebApplication, and
                    // runs the entry assembly's hosting startup.
                    if (SymbolEqualityComparer.Default.Equals(local.Type, types.WebApplicationBuilder) && value is InvocationExpressionSyntax)
                    {
                        scope.Builder = local;
                        builderIndex = i;
                    }

                    continue;
                }

                if (scope.App is null && SymbolEqualityComparer.Default.Equals(local.Type, types.WebApplication) && StartsFrom(value, scope.Builder))
                {
                    scope.App = local;
                    appDeclaration = body[i];
                    continue;
                }

                if (scope.App is null &&
                    value is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax aliased, Name.Identifier.Text: var part } &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(aliased, cancellationToken).Symbol, scope.Builder) &&
                    part switch
                    {
                        "Configuration" => ModuleRewrite.BuilderConfiguration,
                        "Services" => ModuleRewrite.BuilderServices,
                        "Environment" => ModuleRewrite.BuilderEnvironment,
                        _ => (ModuleRewrite?)null,
                    } is { } alias)
                {
                    scope.Aliases[local] = (alias, body[i]);
                    continue;
                }

                if (MayMove(declaration))
                {
                    scope.Locals[local] = body[i];
                    if (scope.App is not null &&
                        types.EndpointRouteBuilder is not null &&
                        IsOrImplements(local.Type, types.EndpointRouteBuilder) &&
                        StartsFrom(value, scope.App))
                    {
                        scope.RouteBuilders.Add(local);
                    }
                }
            }

            if (scope.Builder is null)
            {
                return;
            }

            Track(body, scope);

            // A statement after one that adds configuration sources read the configuration with
            // them; in the module, which runs first, it would read it without.
            var lastConfigurationChange = -1;
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var end = body.Count;
            for (var i = builderIndex + 1; i < body.Count; i++)
            {
                // Nothing after Run() ran before; in the module it would.
                if (scope.App is not null && end == body.Count && Runs(body[i].Statement, scope.App))
                {
                    end = i;
                }

                if (body[i].Chain is not (_, var calls))
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(body[i].StartSymbol, scope.Builder) &&
                    calls.Count > 0 &&
                    calls[0].Access.Name.Identifier.Text is "Configuration" or "Host" or "WebHost" &&
                    calls.Any(call => call.Invocation is not null))
                {
                    lastConfigurationChange = i;
                }
                else if (scope.App is not null && SymbolEqualityComparer.Default.Equals(body[i].StartSymbol, scope.App))
                {
                    // What the program places itself, written here or inside an own app.UseApi().
                    foreach (var call in calls)
                    {
                        if (PlacedMiddleware.Contains(call.Access.Name.Identifier.Text))
                        {
                            placed.Add(call.Access.Name.Identifier.Text);
                        }
                        else if (call.Invocation is { } used &&
                                 model.GetSymbolInfo(used, cancellationToken).Symbol is IMethodSymbol own &&
                                 isOwn(own.ContainingAssembly))
                        {
                            placed.UnionWith(Places(own, depth: 0));
                        }
                    }
                }
            }

            var candidates = new List<Candidate>();
            for (var i = builderIndex + 1; i < end; i++)
            {
                if (body[i] != appDeclaration &&
                    Portable(body[i].Statement) &&
                    CandidateFor(body[i], scope) is { } candidate &&
                    !(i > lastConfigurationChange && lastConfigurationChange >= 0 && ReadsConfigurationEagerly(candidate)))
                {
                    candidates.Add(candidate);
                }
            }

            var moved = SettleInOrder(candidates, body, scope, builderIndex);
            Add(moved);

            // An alias only moved code used now has nothing left to do in the program.
            var movedNodes = MovedNodes(moved);
            foreach (var alias in scope.Aliases)
            {
                if (Users(alias.Key, body) is { Count: > 0 } users && users.All(user => movedNodes.Contains(user.Statement)))
                {
                    Plan.Removed.Add(alias.Value.Declaration.Removable);
                }
            }

            if (scope.App is not null &&
                !placed.Contains("UseRouting") &&
                moved.Any(candidate => candidate.IsMapping) &&
                !KeepsEndpoints(body.GetRange(builderIndex + 1, end - builderIndex - 1), movedNodes, scope))
            {
                Plan.RoutingAfter = appDeclaration!.Statement;
                Plan.ApplicationName = scope.App.Name;

                // WebApplication adds authentication, authorization and antiforgery by itself, before
                // the program's pipeline: before an explicit UseRouting. UseModuleRouting adds them
                // after it, as it would have — unless the program places them itself.
                // Each on its own, as WebApplication decides about each on its own.
                var skipped = new List<string>();
                if (placed.Contains("UseAuthentication"))
                {
                    skipped.Add("authentication: false");
                }

                if (placed.Contains("UseAuthorization"))
                {
                    skipped.Add("authorization: false");
                }

                if (placed.Contains("UseAntiforgery"))
                {
                    skipped.Add("antiforgery: false");
                }

                Plan.RoutingCall = types.ModuleRouting is null || skipped.Count == 3
                    ? "UseRouting()"
                    : "UseModuleRouting(" + string.Join(", ", skipped) + ")";
            }
        }

        /// <summary>A startup class's <c>ConfigureServices(IServiceCollection services)</c>.</summary>
        public void Services(BlockSyntax body, IMethodSymbol method)
        {
            var entries = Read(body.Statements.Select(statement => (statement, (SyntaxNode)statement)));
            var scope = new Scope { Enclosing = method.ContainingType, Services = method.Parameters[0] };
            foreach (var entry in entries)
            {
                if (entry.Declares is { } local && MayMove((LocalDeclarationStatementSyntax)entry.Statement))
                {
                    scope.Locals[local] = entry;
                }
            }

            Track(entries, scope);
            var candidates = entries.Where(entry => Portable(entry.Statement)).Select(entry => CandidateFor(entry, scope)).OfType<Candidate>().ToList();
            Add(SettleInOrder(candidates, entries, scope, builderIndex: -1));
        }

        /// <summary>The body's statements, with what the analysis asks of each read once.</summary>
        private List<Entry> Read(IEnumerable<(StatementSyntax Statement, SyntaxNode Removable)> statements)
        {
            var entries = new List<Entry>();
            foreach (var (statement, removable) in statements)
            {
                var entry = new Entry(statement, removable);
                if (statement is LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables })
                {
                    entry.Declares = model.GetDeclaredSymbol(variables[0], cancellationToken) as ILocalSymbol;
                }
                else if (statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } && Chain(invocation) is { } chain)
                {
                    entry.Chain = chain;
                    entry.StartSymbol = model.GetSymbolInfo(chain.Start, cancellationToken).Symbol;
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// What each statement uses of the builder, the service collection, the aliases and the locals
        /// that could move: bound once, and only the names that could be one of them.
        /// </summary>
        private void Track(List<Entry> body, Scope scope)
        {
            var tracked = new HashSet<ISymbol>(scope.Locals.Keys.Concat<ISymbol>(scope.Aliases.Keys), SymbolEqualityComparer.Default);
            if (scope.Builder is not null)
            {
                tracked.Add(scope.Builder);
            }

            if (scope.Services is not null)
            {
                tracked.Add(scope.Services);
            }

            var names = new HashSet<string>(tracked.Select(symbol => symbol.Name), StringComparer.Ordinal);
            foreach (var entry in body)
            {
                foreach (var identifier in entry.Statement.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (names.Contains(identifier.Identifier.Text) &&
                        model.GetSymbolInfo(identifier, cancellationToken).Symbol is { } symbol &&
                        tracked.Contains(symbol))
                    {
                        entry.Uses.Add(symbol);
                    }
                }
            }
        }

        /// <summary>What a statement is, if it can move: a registration, a mapping, a local, an <c>if</c> around registrations.</summary>
        private Candidate? CandidateFor(Entry entry, Scope scope)
        {
            if (entry.Chain is (var start, var calls))
            {
                return FromServices(entry.StartSymbol, scope) is { } fromServices
                    ? Registration(entry, start, calls, scope, fromServices) ?? Split(entry, calls, scope, fromServices)
                    : OnApplication(entry.StartSymbol, scope) ? Mapping(entry, entry.Statement, start, calls, scope) : null;
            }

            if (entry.Declares is { } local && scope.Locals.ContainsKey(local))
            {
                return scope.RouteBuilders.Contains(local) ? RouteBuilder(entry, local, scope) : Local(entry, local, scope);
            }

            return entry.Statement is IfStatementSyntax conditional ? Conditional(entry, conditional, scope) : null;
        }

        /// <summary>
        /// Whether a chain starts from the service collection — a startup's, or an alias of the
        /// builder's (true) — from the builder (false), or from neither (null).
        /// </summary>
        private static bool? FromServices(ISymbol? start, Scope scope) =>
            start is null ? null :
            SymbolEqualityComparer.Default.Equals(start, scope.Builder) ? false :
            SymbolEqualityComparer.Default.Equals(start, scope.Services) ||
            (start is ILocalSymbol local && scope.Aliases.TryGetValue(local, out var alias) && alias.Rewrite == ModuleRewrite.BuilderServices) ? true :
            (bool?)null;

        /// <summary>Whether a chain starts from the application or from a route builder made from it.</summary>
        private static bool OnApplication(ISymbol? start, Scope scope) =>
            scope.App is not null &&
            (SymbolEqualityComparer.Default.Equals(start, scope.App) || (start is ILocalSymbol routes && scope.RouteBuilders.Contains(routes)));

        /// <summary>Whether an expression is a call chain that starts from <paramref name="symbol"/>.</summary>
        private bool StartsFrom(ExpressionSyntax? value, ISymbol symbol) =>
            value is InvocationExpressionSyntax invocation &&
            Chain(invocation) is (var start, _) &&
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(start, cancellationToken).Symbol, symbol);

        /// <summary>A local that could move: initialized, and not disposed at the end of the body.</summary>
        private static bool MayMove(LocalDeclarationStatementSyntax declaration) =>
            declaration.Declaration.Variables[0].Initializer is not null && declaration.UsingKeyword.IsKind(SyntaxKind.None);

        /// <summary>
        /// <see cref="Settle"/>, then each candidate held to what it needs of the order: the first
        /// that cannot have it is dropped, and the rest settled again without it.
        /// </summary>
        private List<Candidate> SettleInOrder(List<Candidate> candidates, List<Entry> body, Scope scope, int builderIndex)
        {
            while (true)
            {
                var moved = Settle(candidates, body);
                var movedNodes = MovedNodes(moved);
                var broken = moved.FirstOrDefault(candidate => candidate.Order switch
                {
                    OrderRule.NothingBeforeStays => body.Skip(builderIndex + 1)
                        .TakeWhile(entry => entry.Statement.SpanStart < candidate.Node.SpanStart)
                        .Any(entry => !movedNodes.Contains(entry.Statement) && Registers(entry, scope)),
                    OrderRule.AfterItsRegistration => !moved.Any(other =>
                        other.Node.SpanStart < candidate.Node.SpanStart && SymbolEqualityComparer.Default.Equals(other.Registers, candidate.Decorates)),
                    _ => false,
                });

                if (broken is null)
                {
                    return moved;
                }

                candidates = [.. candidates.Where(candidate => candidate != broken)];
            }
        }

        /// <summary>Whether a statement that stays may register something: it touches the builder or the service collection.</summary>
        private static bool Registers(Entry entry, Scope scope) =>
            !(entry.Declares is { } declared && (scope.Aliases.ContainsKey(declared) || SymbolEqualityComparer.Default.Equals(declared, scope.App))) &&
            entry.Uses.Any(symbol =>
                SymbolEqualityComparer.Default.Equals(symbol, scope.Builder) ||
                SymbolEqualityComparer.Default.Equals(symbol, scope.Services) ||
                (symbol is ILocalSymbol local && scope.Aliases.ContainsKey(local)));

        /// <summary>The statements that move whole: not a chain cut in two, which stays in the program.</summary>
        private static HashSet<SyntaxNode> MovedNodes(IEnumerable<Candidate> moved) =>
            [.. moved.Where(candidate => candidate.Pieces.IsEmpty).Select(candidate => candidate.Node)];

        /// <summary>
        /// The mappings in the <c>UseEndpoints</c> a startup class's <c>Configure</c> ends with. Only
        /// when it ends with it: the module's endpoints come after the whole pipeline, and middleware
        /// written after <c>UseEndpoints</c> — a SPA fallback, a terminal <c>Run</c> — would answer first.
        /// </summary>
        public void Endpoints(BlockSyntax body, IMethodSymbol method)
        {
            if (body.Statements.LastOrDefault() is not ExpressionStatementSyntax
                {
                    Expression: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "UseEndpoints", Expression: IdentifierNameSyntax receiver },
                        ArgumentList.Arguments: { Count: 1 } arguments,
                    },
                } useEndpoints ||
                !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, method.Parameters[0]) ||
                arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda ||
                model.GetDeclaredSymbol(lambda.Parameter, cancellationToken) is not { } endpoints ||
                !Portable(useEndpoints))
            {
                return;
            }

            var scope = new Scope { Enclosing = method.ContainingType };
            if (lambda.Body is BlockSyntax block)
            {
                var moved = Read(block.Statements.Select(statement => (statement, (SyntaxNode)statement)))
                    .Select(entry => entry.Chain is (var start, var calls) && SymbolEqualityComparer.Default.Equals(entry.StartSymbol, endpoints)
                        ? Mapping(entry, entry.Statement, start, calls, scope)
                        : null)
                    .OfType<Candidate>()
                    .ToList();
                if (moved.Count == 0)
                {
                    return;
                }

                if (moved.Count == block.Statements.Count)
                {
                    // Nothing of the program's left in it: the module's own UseEndpoints, at the same
                    // place in the pipeline, replaces it.
                    Plan.Mappings.AddRange(moved.Select(candidate => new MovedStatement(candidate.Node, candidate.Rewrites)));
                    Plan.Removed.Add(useEndpoints);
                }
                else
                {
                    Add(moved);
                }
            }
            else if (lambda.Body is InvocationExpressionSyntax expression &&
                     Chain(expression) is (var start, var calls) &&
                     SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(start, cancellationToken).Symbol, endpoints) &&
                     Mapping(entry: null, expression, start, calls, scope) is { } candidate)
            {
                Plan.Mappings.Add(new MovedStatement(candidate.Node, candidate.Rewrites));
                Plan.Removed.Add(useEndpoints);
            }
        }

        /// <summary>A statement that runs the application: <c>app.Run()</c>, <c>await app.RunAsync()</c> and the like.</summary>
        private bool Runs(StatementSyntax statement, ILocalSymbol app) =>
            statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access =>
                access.Name.Identifier.Text is "Run" or "RunAsync" or "Start" or "StartAsync" &&
                access.Expression is IdentifierNameSyntax receiver &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, app));

        private void Add(IEnumerable<Candidate> moved)
        {
            foreach (var candidate in moved)
            {
                if (!candidate.Pieces.IsEmpty)
                {
                    Plan.Registrations.AddRange(candidate.Pieces);
                    Plan.Kept[candidate.Chain!] = candidate.Kept!;
                    continue;
                }

                (candidate.IsMapping ? Plan.Mappings : Plan.Registrations).Add(new MovedStatement(candidate.Node, candidate.Rewrites));
                Plan.Removed.Add(candidate.Entry!.Removable);
            }
        }

        /// <summary>
        /// A registration: from the builder or its services, or from a startup's service collection,
        /// whose first call, and every later one on the builder or the collection, is the project's own;
        /// <paramref name="fromServices"/> when the chain starts from the collection itself.
        /// </summary>
        private Candidate? Registration(Entry entry, IdentifierNameSyntax start, List<Call> calls, Scope scope, bool fromServices)
        {
            var first = First(calls, fromServices);
            if (calls.Count <= first ||
                calls[first].Invocation is not { } invocation ||
                model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
                (!fromServices && first == 0 && !Accepts(method, types.HostApplicationBuilder)))
            {
                return null;
            }

            var order = OrderRule.None;
            ITypeSymbol? decorates = null;
            var foreign = false;
            for (var i = first; i < calls.Count; i++)
            {
                if (calls[i].Invocation is not { } call)
                {
                    continue;
                }

                if (model.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol called ||
                    !Allowed(called, call, ref order, ref decorates) ||
                    SymbolEqualityComparer.Default.Equals(Receiver(called), types.WebApplicationBuilder))
                {
                    return null;
                }

                // A later call on the builder or the collection registers for the whole container as
                // the first does; one on what an earlier call returned configures that.
                var ownership = i == first || OnContainer(called) || isOwn(called.ContainingAssembly)
                    ? Owns(called, call, model, scope, depth: 0)
                    : IsApplicationWide(called.Name) ? Ownership.NotOwn : Ownership.Own;
                if (ownership == Ownership.NotOwn)
                {
                    return null;
                }

                foreign |= ownership == Ownership.ForeignContract;
            }

            // A contract of someone else's — IProfileService, IClaimsTransformation — is one the
            // host's own registrations may register too, and the last registration wins. In the
            // module, which runs first, this one would come first and lose.
            if (foreign && order == OrderRule.None)
            {
                order = OrderRule.NothingBeforeStays;
            }

            var rewrites = new Dictionary<SyntaxNode, ModuleRewrite> { [start] = fromServices ? ModuleRewrite.BuilderServices : ModuleRewrite.Builder };
            var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            return Check(entry.Statement, start, scope, registration: true, rewrites, locals)
                ? new Candidate(entry, entry.Statement, isMapping: false, declares: null, [.. rewrites], locals)
                {
                    Order = order,
                    Decorates = decorates,
                    Registers = RegisteredService(method, invocation, model),
                }
                : null;
        }

        /// <summary>
        /// Where a chain's calls begin: after <c>.Services</c> when it starts from the builder's,
        /// else right away — on the service collection, or on the builder for its own extensions.
        /// </summary>
        private int First(List<Call> calls, bool fromServices) =>
            !fromServices &&
            calls.Count > 0 &&
            calls[0].Invocation is null &&
            calls[0].Access.Name.Identifier.Text == "Services" &&
            model.GetSymbolInfo(calls[0].Access, cancellationToken).Symbol is IPropertySymbol
                ? 1
                : 0;

        /// <summary>Whether a call registers for the whole container: an extension of the service collection or of the builder.</summary>
        private bool OnContainer(IMethodSymbol method) =>
            Accepts(method, types.ServiceCollection) || Accepts(method, types.HostApplicationBuilder);

        /// <summary>
        /// Whether a call may move for what it does to the order: never one that replaces or removes
        /// a registration; a <c>PostConfigure</c> on the project's own options, which runs after
        /// every <c>Configure</c> wherever it is registered; a <c>Decorate</c> of the project's own
        /// service, with the registration it decorates.
        /// </summary>
        private bool Allowed(IMethodSymbol called, InvocationExpressionSyntax call, ref OrderRule order, ref ITypeSymbol? decorates)
        {
            if (OrderSensitivePrefixes.Any(prefix => called.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return false;
            }

            if (called.Name.StartsWith("PostConfigure", StringComparison.Ordinal))
            {
                var options = Receiver(called) is INamedTypeSymbol { Name: "OptionsBuilder", TypeArguments.Length: 1 } builder
                    ? builder.TypeArguments[0]
                    : called.TypeArguments.FirstOrDefault();
                return options is not null && NamesOwnType(options);
            }

            if (called.Name == "Decorate")
            {
                var service = called.TypeArguments.FirstOrDefault() ??
                    (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is TypeOfExpressionSyntax typeOf ? model.GetTypeInfo(typeOf.Type, cancellationToken).Type : null);
                if (service is null || !NamesOwnType(service))
                {
                    return false;
                }

                order = OrderRule.AfterItsRegistration;
                decorates = service;
            }

            return true;
        }

        /// <summary>
        /// The service a plain <c>AddSingleton</c>, <c>AddScoped</c> or <c>AddTransient</c> (keyed
        /// or not) registers: its first type argument, or its first argument's <c>typeof</c>.
        /// </summary>
        private ITypeSymbol? RegisteredService(IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            var definition = method.ReducedFrom ?? method;
            if (definition.ContainingNamespace?.ToDisplayString() != "Microsoft.Extensions.DependencyInjection" ||
                definition.Name is not ("AddSingleton" or "AddScoped" or "AddTransient" or "AddKeyedSingleton" or "AddKeyedScoped" or "AddKeyedTransient"))
            {
                return null;
            }

            if (method.TypeArguments.Length > 0)
            {
                return method.TypeArguments[0];
            }

            return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is TypeOfExpressionSyntax typeOf
                ? semanticModel.GetTypeInfo(typeOf.Type, cancellationToken).Type
                : null;
        }

        /// <summary>
        /// A chain from the service collection whose every call takes and returns it — a statement
        /// per call in all but syntax — cut in two: the project's own calls move, each a statement of
        /// its own, and the program keeps the others in their order.
        /// </summary>
        private Candidate? Split(Entry entry, List<Call> calls, Scope scope, bool fromServices)
        {
            var first = First(calls, fromServices);
            if ((!fromServices && first == 0) || calls.Count - first < 2)
            {
                return null;
            }

            var moving = new List<int>();
            for (var i = first; i < calls.Count; i++)
            {
                if (calls[i].Invocation is not { } call ||
                    model.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol called ||
                    !Accepts(called, types.ServiceCollection) ||
                    !SymbolEqualityComparer.Default.Equals(called.ReturnType, types.ServiceCollection))
                {
                    return null;
                }

                // A piece runs first in the module whatever stays before it, so not someone else's contract.
                var order = OrderRule.None;
                ITypeSymbol? decorates = null;
                if (Allowed(called, call, ref order, ref decorates) &&
                    order == OrderRule.None &&
                    Owns(called, call, model, scope, depth: 0) == Ownership.Own)
                {
                    moving.Add(i);
                }
            }

            if (moving.Count == 0 || moving.Count == calls.Count - first)
            {
                return null;
            }

            var pieces = new List<MovedStatement>();
            foreach (var i in moving)
            {
                // The call's own arguments only: what the program keeps is not the module's business.
                var rewrites = new Dictionary<SyntaxNode, ModuleRewrite> { [calls[i].Access.Expression] = ModuleRewrite.BuilderServices };
                var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
                if (!Check(calls[i].Invocation!.ArgumentList, start: null, scope, registration: true, rewrites, locals) ||
                    (calls[i].Access.Name is GenericNameSyntax generic && !Check(generic.TypeArgumentList, start: null, scope, registration: true, rewrites, locals)) ||
                    locals.Count > 0)
                {
                    return null;
                }

                pieces.Add(new MovedStatement(calls[i].Invocation!, [.. rewrites]) { IsLink = true });
            }

            ExpressionSyntax kept = calls[first].Access.Expression;
            for (var i = first; i < calls.Count; i++)
            {
                if (!moving.Contains(i))
                {
                    kept = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, kept, calls[i].Access.OperatorToken, calls[i].Access.Name),
                        calls[i].Invocation!.ArgumentList);
                }
            }

            return new Candidate(entry, entry.Statement, isMapping: false, declares: null, [], new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default))
            {
                Pieces = [.. pieces],
                Kept = kept,
                Chain = ((ExpressionStatementSyntax)entry.Statement).Expression,
            };
        }

        /// <summary>
        /// <c>if (builder.Environment.IsDevelopment()) builder.Services.AddXxx&lt;Own&gt;();</c>: a
        /// condition the module evaluates as the program did — it uses only what a moving statement
        /// may: the builder's configuration and environment, locals that move with it, static members —
        /// around registrations that would move on their own.
        /// </summary>
        private Candidate? Conditional(Entry entry, IfStatementSyntax conditional, Scope scope)
        {
            if (conditional.Else is not null)
            {
                return null;
            }

            var statements = conditional.Statement is BlockSyntax block ? [.. block.Statements] : new List<StatementSyntax> { conditional.Statement };
            if (statements.Count == 0)
            {
                return null;
            }

            var rewrites = new Dictionary<SyntaxNode, ModuleRewrite>();
            var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            if (!Check(conditional.Condition, start: null, scope, registration: true, rewrites, locals))
            {
                return null;
            }

            foreach (var statement in Read(statements.Select(statement => (statement, (SyntaxNode)statement))))
            {
                if (statement.Chain is not (var start, var calls) ||
                    FromServices(statement.StartSymbol, scope) is not { } fromServices ||
                    Registration(statement, start, calls, scope, fromServices) is not { Order: OrderRule.None } inner)
                {
                    return null;
                }

                foreach (var pair in inner.Rewrites)
                {
                    rewrites[pair.Key] = pair.Value;
                }

                locals.UnionWith(inner.Locals);
            }

            return new Candidate(entry, entry.Statement, isMapping: false, declares: null, [.. rewrites], locals);
        }

        /// <summary>
        /// Whose code a call registers, read as it would be in the program: nothing that shapes the
        /// whole application or acts on what was registered before, and only what names the
        /// project's code — under a contract of its own, or of someone else's. An own method
        /// registers what its body does.
        /// </summary>
        private Ownership Owns(IMethodSymbol called, InvocationExpressionSyntax call, SemanticModel callModel, Scope? scope, int depth)
        {
            // AddServiceDefaults is the whole application's even where the solution wrote it.
            if (IsApplicationWide(called.Name))
            {
                return Ownership.NotOwn;
            }

            if (isOwn(called.ContainingAssembly))
            {
                return depth <= 4 ? Body(called, depth) : Ownership.Own;
            }

            if (OrderSensitivePrefixes.Any(prefix => called.Name.StartsWith(prefix, StringComparison.Ordinal)) ||
                !NamesOwnCode(called, call, callModel, scope))
            {
                return Ownership.NotOwn;
            }

            return RegisteredService(called, call, callModel) is { } service && !NamesOwnType(service) ? Ownership.ForeignContract : Ownership.Own;
        }

        /// <summary>
        /// What an own method registers: its calls on the builder or the service collection, and the
        /// own methods it calls, each as <see cref="Owns"/> reads it; a call that configures the host
        /// — logging, metrics, configuration sources — makes it not the project's alone. A method
        /// whose source is not in view is taken at its word.
        /// </summary>
        private Ownership Body(IMethodSymbol method, int depth)
        {
            method = (method.ReducedFrom ?? method).OriginalDefinition;
            if (_bodies.TryGetValue(method, out var known))
            {
                return known;
            }

            _bodies[method] = Ownership.Own;
            var ownership = Ownership.Own;
            foreach (var (declaration, bodyModel) in Declarations(method))
            {
                foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (ownership == Ownership.NotOwn)
                    {
                        break;
                    }

                    if (bodyModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                    {
                        continue;
                    }

                    var registered = isOwn(called.ContainingAssembly) || OnContainer(called)
                        ? Owns(called, invocation, bodyModel, scope: null, depth + 1)
                        : Receiver(called) is { } receiver && types.ApplicationWideReceivers.Contains(receiver.OriginalDefinition)
                            ? Ownership.NotOwn
                            : Ownership.Own;
                    if (registered > ownership)
                    {
                        ownership = registered;
                    }
                }
            }

            _bodies[method] = ownership;
            return ownership;
        }

        /// <summary>
        /// Whether the program surely maps something of its own after the move: a framework mapping
        /// written in it, on the application, outside any condition. Then <c>WebApplication</c> adds
        /// routing by itself, as before, and the module's <c>UseEndpoints</c> finds it.
        /// </summary>
        /// <remarks>
        /// Only what is sure. A mapping inside a method — eShop's shared <c>MapDefaultEndpoints()</c>
        /// maps <c>/health</c> in Development only — may map nothing, and then the module's
        /// <c>UseEndpoints</c> throws at startup for want of routing. A routing call too many costs
        /// nothing; one too few stops the application.
        /// </remarks>
        private bool KeepsEndpoints(IEnumerable<Entry> statements, HashSet<SyntaxNode> movedNodes, Scope scope) =>
            statements.Any(entry =>
                !movedNodes.Contains(entry.Statement) &&
                entry.Chain is (_, var calls) &&
                OnApplication(entry.StartSymbol, scope) &&
                calls.Any(call =>
                    FrameworkMappings.Contains(call.Access.Name.Identifier.Text) &&
                    call.Invocation is { } used &&
                    model.GetSymbolInfo(used, cancellationToken).Symbol is IMethodSymbol mapping &&
                    !isOwn(mapping.ContainingAssembly) &&
                    mapping.DeclaringSyntaxReferences.IsEmpty));

        /// <summary>The middleware a program places that decides where <c>WebApplication</c>'s implicit middleware goes.</summary>
        private static readonly ImmutableHashSet<string> PlacedMiddleware = ["UseRouting", "UseAuthentication", "UseAuthorization", "UseAntiforgery"];

        /// <summary>
        /// What an own method of the application's surely places of <see cref="PlacedMiddleware"/>,
        /// itself or through the own methods it calls. Not what it places under a condition, as the
        /// program's own statements in an <c>if</c> do not count: what may not run, WebApplication
        /// adds itself when it does not, and so must UseModuleRouting. One too many costs nothing.
        /// </summary>
        private HashSet<string> Places(IMethodSymbol method, int depth)
        {
            method = (method.ReducedFrom ?? method).OriginalDefinition;
            if (_places.TryGetValue(method, out var placed))
            {
                return placed;
            }

            _places[method] = placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (declaration, bodyModel) in Declarations(method))
            {
                foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (MayNotRun(invocation, declaration) ||
                        bodyModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called)
                    {
                        continue;
                    }

                    if (PlacedMiddleware.Contains(called.Name))
                    {
                        placed.Add(called.Name);
                    }
                    else if (depth < 4 && isOwn(called.ContainingAssembly))
                    {
                        placed.UnionWith(Places(called, depth + 1));
                    }
                }
            }

            return placed;
        }

        /// <summary>Whether a node of a body may not run when the body does: under a condition, in a loop, a catch, a lambda or a local function.</summary>
        private static bool MayNotRun(SyntaxNode node, SyntaxNode body) =>
            node.Ancestors().TakeWhile(ancestor => ancestor != body).Any(ancestor =>
                ancestor is IfStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or ConditionalExpressionSyntax
                    or ConditionalAccessExpressionSyntax or CommonForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax
                    or CatchClauseSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax ||
                ancestor.IsKind(SyntaxKind.LogicalAndExpression) ||
                ancestor.IsKind(SyntaxKind.LogicalOrExpression) ||
                ancestor.IsKind(SyntaxKind.CoalesceExpression));

        /// <summary>
        /// A method's declarations with a semantic model for each: from its symbol when it is this
        /// compilation's own, else from <see cref="Sources"/>. Never another compilation's source
        /// through its symbol: in an IDE a project reference is a compilation, in a build it is
        /// metadata, and the analyzer must answer alike. The code fix reaches it through Sources.
        /// </summary>
        private List<(SyntaxNode Declaration, SemanticModel Model)> Declarations(IMethodSymbol method)
        {
            var declarations = SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, model.Compilation.Assembly)
                ? method.DeclaringSyntaxReferences.Select(reference => (reference.GetSyntax(cancellationToken), model.Compilation))
                : Sources?.Invoke(method) ?? [];
            return [.. declarations.Select(pair => (pair.Item1, ModelOf(pair.Item1.SyntaxTree, pair.Item2)))];
        }

        /// <summary>One semantic model per tree, however many methods are read from it.</summary>
        private SemanticModel ModelOf(SyntaxTree tree, Compilation compilation)
        {
            if (tree == model.SyntaxTree)
            {
                return model;
            }

            if (!_models.TryGetValue(tree, out var treeModel))
            {
                _models[tree] = treeModel = compilation.GetSemanticModel(tree);
            }

            return treeModel;
        }

        /// <summary>
        /// A mapping: from the application or a route builder, with an <c>IEndpointRouteBuilder</c>
        /// extension, mapping something of the project's.
        /// </summary>
        private Candidate? Mapping(Entry? entry, SyntaxNode node, IdentifierNameSyntax start, List<Call> calls, Scope scope)
        {
            if (calls.Count == 0 ||
                calls[0].Invocation is not { } first ||
                model.GetSymbolInfo(first, cancellationToken).Symbol is not IMethodSymbol method ||
                !OnRouteBuilder(method))
            {
                return null;
            }

            var own = false;
            foreach (var call in calls)
            {
                if (call.Invocation is not { } invocation)
                {
                    continue;
                }

                if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol called ||
                    ApplicationWideMappings.Contains(called.Name))
                {
                    return null;
                }

                own |= isOwn(called.ContainingAssembly) ||
                    called.TypeArguments.Any(NamesOwnType) ||
                    (OnRouteBuilder(called) && invocation.ArgumentList.Arguments.Any(argument => IsOwnHandler(argument.Expression)));
            }

            if (!own)
            {
                return null;
            }

            var rewrites = new Dictionary<SyntaxNode, ModuleRewrite>();
            var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            if (model.GetSymbolInfo(start, cancellationToken).Symbol is ILocalSymbol routes && scope.RouteBuilders.Contains(routes))
            {
                locals.Add(routes);
            }
            else
            {
                rewrites[start] = ModuleRewrite.Endpoints;
            }

            return Check(node, start, scope, registration: false, rewrites, locals)
                ? new Candidate(entry, node, isMapping: true, declares: null, [.. rewrites], locals)
                : null;
        }

        /// <summary>A route builder made from the application, which moves with the mappings that use it.</summary>
        private Candidate? RouteBuilder(Entry entry, ILocalSymbol local, Scope scope)
        {
            if (((LocalDeclarationStatementSyntax)entry.Statement).Declaration.Variables[0].Initializer?.Value is not InvocationExpressionSyntax made ||
                Chain(made) is not (var start, var calls) ||
                calls.Count == 0 ||
                calls[0].Invocation is not { } first ||
                model.GetSymbolInfo(first, cancellationToken).Symbol is not IMethodSymbol method ||
                !OnRouteBuilder(method))
            {
                return null;
            }

            var rewrites = new Dictionary<SyntaxNode, ModuleRewrite> { [start] = ModuleRewrite.Endpoints };
            var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            return Check(entry.Statement, start, scope, registration: false, rewrites, locals)
                ? new Candidate(entry, entry.Statement, isMapping: true, declares: local, [.. rewrites], locals)
                : null;
        }

        /// <summary>A call made on a route builder: an extension of <c>IEndpointRouteBuilder</c> or of a type implementing it.</summary>
        /// <remarks>
        /// Not one made on the application itself, which is a route builder and a pipeline at once:
        /// an <c>app.UseApi()</c> of the project's may add middleware as well as endpoints.
        /// </remarks>
        private bool OnRouteBuilder(IMethodSymbol method) =>
            types.EndpointRouteBuilder is not null &&
            (Accepts(method, types.EndpointRouteBuilder) ||
             (Receiver(method) is { } receiver and not ITypeParameterSymbol &&
              IsOrImplements(receiver, types.EndpointRouteBuilder) &&
              !IsOrImplements(receiver, types.ApplicationBuilder)));

        /// <summary>A local that may move with the registrations that use it.</summary>
        private Candidate? Local(Entry entry, ILocalSymbol local, Scope scope)
        {
            var rewrites = new Dictionary<SyntaxNode, ModuleRewrite>();
            var locals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            return Check(entry.Statement, start: null, scope, registration: true, rewrites, locals)
                ? new Candidate(entry, entry.Statement, isMapping: false, local, [.. rewrites], locals)
                : null;
        }

        /// <summary>
        /// A handler written in the project: a lambda or an anonymous method in place, or a method
        /// group of the project's own code.
        /// </summary>
        private bool IsOwnHandler(ExpressionSyntax expression) =>
            expression is AnonymousFunctionExpressionSyntax ||
            (model.GetSymbolInfo(expression, cancellationToken).Symbol is IMethodSymbol { MethodKind: not MethodKind.LocalFunction } handler &&
             isOwn(handler.ContainingAssembly));

        /// <summary>
        /// Whether a call names the project's own code: an own type as a type argument, or anywhere in
        /// its arguments — <c>typeof</c>, a constructed object, a lambda's body — or an own method.
        /// </summary>
        private bool NamesOwnCode(IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel semanticModel, Scope? scope) =>
            method.TypeArguments.Any(NamesOwnType) || NamesOwnCode(invocation.ArgumentList, semanticModel, scope, followLocals: true);

        /// <summary>
        /// Whether a node names the project's code anywhere: an own type, an own method,
        /// <c>Assembly.GetExecutingAssembly()</c> — the project's assembly, from the module as from
        /// the program — or a local of the program initialized with one of those.
        /// </summary>
        private bool NamesOwnCode(SyntaxNode node, SemanticModel semanticModel, Scope? scope, bool followLocals)
        {
            foreach (var descendant in node.DescendantNodes())
            {
                switch (descendant)
                {
                    case SimpleNameSyntax name when name.Parent is not NameColonSyntax:
                        switch (semanticModel.GetSymbolInfo(name, cancellationToken).Symbol)
                        {
                            case ITypeSymbol type when NamesOwnType(type):
                                return true;
                            case IMethodSymbol { Name: "GetExecutingAssembly", ContainingType: { Name: "Assembly", ContainingNamespace: { Name: "Reflection", ContainingNamespace.Name: "System" } } }:
                                return true;
                            case IMethodSymbol called when called.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction) &&
                                                         (isOwn(called.ContainingAssembly) || called.TypeArguments.Any(NamesOwnType)):
                                return true;
                            case ILocalSymbol local when followLocals &&
                                                         scope is not null &&
                                                         scope.Locals.TryGetValue(local, out var declared) &&
                                                         declared.Statement is LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables } &&
                                                         variables[0].Initializer is { } initializer &&
                                                         NamesOwnCode(initializer, semanticModel, scope, followLocals: false):
                                return true;
                        }

                        break;
                    case ImplicitObjectCreationExpressionSyntax creation when NamesOwnType(semanticModel.GetTypeInfo(creation, cancellationToken).Type):
                        return true;
                }
            }

            return false;
        }

        private bool NamesOwnType(ITypeSymbol? type) => type switch
        {
            IArrayTypeSymbol array => NamesOwnType(array.ElementType),
            IPointerTypeSymbol pointer => NamesOwnType(pointer.PointedAtType),
            INamedTypeSymbol named => (named.ContainingAssembly is { } assembly && isOwn(assembly)) || named.TypeArguments.Any(NamesOwnType),
            _ => false,
        };

        /// <summary>
        /// Whether everything the statement refers to of the code around it has a counterpart in the
        /// module, recording how each reference is written there and which locals it needs.
        /// </summary>
        private bool Check(
            SyntaxNode statement,
            IdentifierNameSyntax? start,
            Scope scope,
            bool registration,
            Dictionary<SyntaxNode, ModuleRewrite> rewrites,
            HashSet<ILocalSymbol> locals)
        {
            var handled = new HashSet<SyntaxNode>();
            foreach (var node in statement.DescendantNodes())
            {
                if (node is BaseExpressionSyntax)
                {
                    return false;
                }

                if (node is ThisExpressionSyntax self)
                {
                    // this.Configuration in a startup: the builder's configuration.
                    if (registration &&
                        self.Parent is MemberAccessExpressionSyntax access &&
                        access.Expression == self &&
                        model.GetSymbolInfo(access.Name, cancellationToken).Symbol is { IsStatic: false } member &&
                        IsWithin(member.ContainingType, scope.Enclosing) &&
                        MemberRewrite(member, access) is { } memberRewrite)
                    {
                        rewrites[access] = memberRewrite;
                        handled.Add(access.Name);
                        continue;
                    }

                    return false;
                }

                if (node is not IdentifierNameSyntax identifier || identifier == start || handled.Contains(identifier) || identifier.Parent is NameColonSyntax)
                {
                    continue;
                }

                var info = model.GetSymbolInfo(identifier, cancellationToken);
                var symbol = info.Symbol;
                if (symbol is null)
                {
                    // nameof is the one name that legitimately binds to nothing.
                    if (identifier.Identifier.Text == "nameof" && identifier.Parent is InvocationExpressionSyntax && info.CandidateSymbols.IsEmpty)
                    {
                        continue;
                    }

                    return false;
                }

                switch (symbol)
                {
                    case ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                        if (DeclaredWithin(symbol, statement))
                        {
                            continue;
                        }

                        // var configuration = builder.Configuration: the module's builder.Configuration.
                        if (symbol is ILocalSymbol aliased && scope.Aliases.TryGetValue(aliased, out var alias))
                        {
                            if (!registration)
                            {
                                return false;
                            }

                            rewrites[identifier] = alias.Rewrite == ModuleRewrite.BuilderEnvironment && NeedsWebEnvironment(identifier) ? ModuleRewrite.WebEnvironment : alias.Rewrite;
                            continue;
                        }

                        if (SymbolEqualityComparer.Default.Equals(symbol, scope.Builder))
                        {
                            if (registration &&
                                identifier.Parent is MemberAccessExpressionSyntax builderAccess &&
                                builderAccess.Expression == identifier &&
                                builderAccess.Name.Identifier.Text is "Services" or "Configuration" or "Environment")
                            {
                                if (builderAccess.Name.Identifier.Text == "Environment" && NeedsWebEnvironment(builderAccess))
                                {
                                    rewrites[builderAccess] = ModuleRewrite.WebEnvironment;
                                    handled.Add(builderAccess.Name);
                                }
                                else
                                {
                                    rewrites[identifier] = ModuleRewrite.Builder;
                                }

                                continue;
                            }

                            return false;
                        }

                        // A registration's local goes to ConfigureServices, a route builder to UseEndpoints.
                        if (symbol is ILocalSymbol local && scope.Locals.ContainsKey(local) && registration != scope.RouteBuilders.Contains(local))
                        {
                            locals.Add(local);
                            continue;
                        }

                        return false;

                    case INamespaceSymbol:
                        continue;

                    case ITypeSymbol type:
                        if (IsWithin(type.ContainingType, scope.Enclosing) && !Reachable(type))
                        {
                            return false;
                        }

                        continue;

                    default:
                        if (!IsWithin(symbol.ContainingType, scope.Enclosing))
                        {
                            continue;
                        }

                        if (!symbol.IsStatic)
                        {
                            // Configuration and Environment of a startup: the builder's own. Anything
                            // else of the instance has no counterpart in the module.
                            if (registration &&
                                !(identifier.Parent is MemberAccessExpressionSyntax qualified && qualified.Name == identifier) &&
                                MemberRewrite(symbol, identifier) is { } memberRewrite)
                            {
                                rewrites[identifier] = memberRewrite;
                                continue;
                            }

                            return false;
                        }

                        if (!Reachable(symbol))
                        {
                            return false;
                        }

                        continue;
                }
            }

            return true;
        }

        /// <summary>What a startup's member is in the module: its configuration or its environment.</summary>
        private ModuleRewrite? MemberRewrite(ISymbol member, ExpressionSyntax use)
        {
            var type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null,
            };

            if (type is null)
            {
                return null;
            }

            if (IsOrImplements(type, types.Configuration))
            {
                return ModuleRewrite.BuilderConfiguration;
            }

            if (IsOrImplements(type, types.HostEnvironment))
            {
                return NeedsWebEnvironment(use) ? ModuleRewrite.WebEnvironment : ModuleRewrite.BuilderEnvironment;
            }

            return null;
        }

        private static bool IsOrImplements(ITypeSymbol type, INamedTypeSymbol? target) =>
            target is not null &&
            (SymbolEqualityComparer.Default.Equals(type, target) || ModuleFacts.Implements(type, target));

        /// <summary>
        /// Whether this use of an environment needs <c>IWebHostEnvironment</c> rather than the
        /// module builder's <c>IHostEnvironment</c>: a member only the web environment has, or a
        /// conversion to it.
        /// </summary>
        private bool NeedsWebEnvironment(ExpressionSyntax use)
        {
            if (types.WebHostEnvironment is null)
            {
                return false;
            }

            if (use.Parent is MemberAccessExpressionSyntax outer && outer.Expression == use)
            {
                var member = model.GetSymbolInfo(outer, cancellationToken).Symbol;
                var owner = member is IMethodSymbol { ReducedFrom: { } extension } ? extension.Parameters[0].Type : member?.ContainingType;
                return SymbolEqualityComparer.Default.Equals(owner, types.WebHostEnvironment);
            }

            return SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(use, cancellationToken).ConvertedType, types.WebHostEnvironment);
        }

        /// <summary>Whether a member of the program's or startup's own type can be reached from another class.</summary>
        private static bool Reachable(ISymbol symbol)
        {
            for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWithin(INamedTypeSymbol? type, INamedTypeSymbol? enclosing)
        {
            for (var current = type; current is not null && enclosing is not null; current = current.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, enclosing))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DeclaredWithin(ISymbol symbol, SyntaxNode node) =>
            symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree == node.SyntaxTree && node.Span.Contains(reference.Span));

        /// <summary>
        /// Whether the candidate reads the configuration when it runs rather than in a lambda that
        /// runs later.
        /// </summary>
        private static bool ReadsConfigurationEagerly(Candidate candidate) =>
            candidate.Rewrites.Any(rewrite =>
                (rewrite.Value == ModuleRewrite.BuilderConfiguration ||
                 (rewrite.Value == ModuleRewrite.Builder && rewrite.Key.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: "Configuration" })) &&
                !rewrite.Key.Ancestors().TakeWhile(ancestor => ancestor != candidate.Node).Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax));

        /// <summary>
        /// A statement that can be cut out and pasted elsewhere: no preprocessor directive in or
        /// around it, and not inside an <c>#if</c>.
        /// </summary>
        private bool Portable(StatementSyntax statement)
        {
            if (statement.ContainsDirectives)
            {
                return false;
            }

            _conditionalDirectives ??= [.. root.DescendantTrivia(descendIntoTrivia: true)
                .Where(trivia => trivia.IsKind(SyntaxKind.IfDirectiveTrivia) || trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia))
                .Select(trivia => trivia.IsKind(SyntaxKind.IfDirectiveTrivia) ? trivia.SpanStart + 1 : -(trivia.SpanStart + 1))];

            var depth = 0;
            foreach (var directive in _conditionalDirectives)
            {
                if (Math.Abs(directive) - 1 >= statement.SpanStart)
                {
                    break;
                }

                depth += directive > 0 ? 1 : -1;
            }

            return depth <= 0;
        }

        /// <summary>
        /// The candidates that move: each with the locals it uses, a local only when nothing that
        /// stays uses it too.
        /// </summary>
        private static List<Candidate> Settle(List<Candidate> candidates, List<Entry> body)
        {
            var moved = new List<Candidate>(candidates.Where(candidate => candidate.Declares is null));
            var available = candidates.Where(candidate => candidate.Declares is not null)
                .ToDictionary(candidate => candidate.Declares!, candidate => candidate, SymbolEqualityComparer.Default);
            var movedLocals = new Dictionary<ILocalSymbol, Candidate>(SymbolEqualityComparer.Default);

            bool changed;
            do
            {
                changed = false;
                var needed = new HashSet<ILocalSymbol>(
                    moved.Concat(movedLocals.Values).SelectMany(candidate => candidate.Locals),
                    SymbolEqualityComparer.Default);

                foreach (var local in needed)
                {
                    if (movedLocals.ContainsKey(local))
                    {
                        continue;
                    }

                    if (available.TryGetValue(local, out var declaration))
                    {
                        movedLocals[local] = declaration;
                    }
                    else
                    {
                        Drop(local);
                    }

                    changed = true;
                }

                // A chain cut in two stays in the program, with whatever its kept calls use.
                var movedStatements = MovedNodes(moved.Concat(movedLocals.Values));
                foreach (var local in movedLocals.Keys.ToList())
                {
                    if (!needed.Contains(local))
                    {
                        movedLocals.Remove(local);
                        changed = true;
                        continue;
                    }

                    if (Users(local, body).Any(user => !movedStatements.Contains(user.Statement)))
                    {
                        movedLocals.Remove(local);
                        Drop(local);
                        changed = true;
                    }
                }
            }
            while (changed);

            return [.. moved.Concat(movedLocals.Values).OrderBy(candidate => candidate.Node.SpanStart)];

            void Drop(ILocalSymbol local)
            {
                available.Remove(local);
                moved.RemoveAll(candidate => candidate.Locals.Contains(local));
                foreach (var dependent in movedLocals.Where(pair => pair.Value.Locals.Contains(local)).Select(pair => pair.Key).ToList())
                {
                    movedLocals.Remove(dependent);
                    available.Remove(dependent);
                }
            }
        }

        /// <summary>The statements of the body other than its declaration that use a local.</summary>
        private static List<Entry> Users(ILocalSymbol local, List<Entry> body) =>
            [.. body.Where(entry => entry.Uses.Contains(local) && !SymbolEqualityComparer.Default.Equals(entry.Declares, local))];
    }

    /// <summary>One link of a fluent chain: a member access, and the call made with it if any.</summary>
    private readonly struct Call(MemberAccessExpressionSyntax access, InvocationExpressionSyntax? invocation)
    {
        public MemberAccessExpressionSyntax Access { get; } = access;

        public InvocationExpressionSyntax? Invocation { get; } = invocation;
    }

    /// <summary>The name a chain starts from and its links, innermost first; null for anything else.</summary>
    private static (IdentifierNameSyntax Start, List<Call> Calls)? Chain(ExpressionSyntax expression)
    {
        var calls = new List<Call>();
        InvocationExpressionSyntax? pending = null;
        var current = expression;
        while (true)
        {
            if (current is InvocationExpressionSyntax invocation && pending is null)
            {
                pending = invocation;
                current = invocation.Expression;
            }
            else if (current is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                calls.Add(new Call(access, pending));
                pending = null;
                current = access.Expression;
            }
            else
            {
                break;
            }
        }

        if (pending is not null || current is not IdentifierNameSyntax start)
        {
            return null;
        }

        calls.Reverse();
        return (start, calls);
    }

    /// <summary>The framework types the analysis compares against, looked up once per compilation.</summary>
    internal sealed class Types
    {
        public INamedTypeSymbol? WebApplication { get; private set; }

        public INamedTypeSymbol? WebApplicationBuilder { get; private set; }

        public INamedTypeSymbol HostApplicationBuilder { get; private set; } = null!;

        public INamedTypeSymbol? EndpointRouteBuilder { get; private set; }

        public INamedTypeSymbol ServiceCollection { get; private set; } = null!;

        public INamedTypeSymbol? ApplicationBuilder { get; private set; }

        public INamedTypeSymbol? Configuration { get; private set; }

        public INamedTypeSymbol? HostEnvironment { get; private set; }

        public INamedTypeSymbol? WebHostEnvironment { get; private set; }

        public INamedTypeSymbol? ModuleBase { get; private set; }

        public INamedTypeSymbol? HostingStartup { get; private set; }

        public INamedTypeSymbol? StartupFilter { get; private set; }

        public INamedTypeSymbol? ModuleRouting { get; private set; }

        /// <summary>The types of <see cref="ProgramConversion.ApplicationWideReceivers"/> the compilation has.</summary>
        public HashSet<ITypeSymbol> ApplicationWideReceivers { get; private set; } = null!;

        /// <summary>The types, or null when the compilation has no service collection or host builder.</summary>
        public static Types? For(Compilation compilation) =>
            compilation.GetTypeByMetadataName(ModuleFacts.HostApplicationBuilderMetadataName) is { } hostApplicationBuilder &&
            compilation.GetTypeByMetadataName(ServiceCollectionMetadataName) is { } serviceCollection
                ? new Types
                {
                    WebApplication = compilation.GetTypeByMetadataName(ModuleFacts.WebApplicationMetadataName),
                    WebApplicationBuilder = compilation.GetTypeByMetadataName(WebApplicationBuilderMetadataName),
                    HostApplicationBuilder = hostApplicationBuilder,
                    EndpointRouteBuilder = compilation.GetTypeByMetadataName(EndpointRouteBuilderMetadataName),
                    ServiceCollection = serviceCollection,
                    ApplicationBuilder = compilation.GetTypeByMetadataName(ModuleFacts.ApplicationBuilderMetadataName),
                    Configuration = compilation.GetTypeByMetadataName(ConfigurationMetadataName),
                    HostEnvironment = compilation.GetTypeByMetadataName(HostEnvironmentMetadataName),
                    WebHostEnvironment = compilation.GetTypeByMetadataName(ModuleFacts.WebHostEnvironmentMetadataName),
                    ModuleBase = ModuleFacts.GetModuleBaseType(compilation),
                    HostingStartup = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupMetadataName),
                    StartupFilter = compilation.GetTypeByMetadataName(ModuleFacts.StartupFilterMetadataName),
                    ModuleRouting = compilation.GetTypeByMetadataName("FusionModules.ModuleRouting"),
                    ApplicationWideReceivers = new HashSet<ITypeSymbol>(
                        ProgramConversion.ApplicationWideReceivers.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>(),
                        SymbolEqualityComparer.Default),
                }
                : null;
    }
}

/// <summary>How a reference to the program is written in the module.</summary>
internal enum ModuleRewrite
{
    /// <summary>The builder: the module's <c>IHostApplicationBuilder</c> parameter.</summary>
    Builder,

    /// <summary>A startup's service collection: <c>builder.Services</c>.</summary>
    BuilderServices,

    /// <summary>A startup's configuration: <c>builder.Configuration</c>.</summary>
    BuilderConfiguration,

    /// <summary>A startup's environment: <c>builder.Environment</c>.</summary>
    BuilderEnvironment,

    /// <summary>An environment used for what only <c>IWebHostEnvironment</c> has: <c>builder.Environment</c>, cast.</summary>
    WebEnvironment,

    /// <summary>The application, as a route builder: the parameter of the module's <c>UseEndpoints</c> lambda.</summary>
    Endpoints,
}

/// <summary>A statement (or a lambda's expression) that moves, and how its references are rewritten.</summary>
internal sealed class MovedStatement(SyntaxNode node, ImmutableArray<KeyValuePair<SyntaxNode, ModuleRewrite>> rewrites)
{
    /// <summary>A statement, or the expression body of a <c>UseEndpoints</c> lambda.</summary>
    public SyntaxNode Node { get; } = node;

    /// <summary>The nodes to replace, each with what replaces it.</summary>
    public ImmutableArray<KeyValuePair<SyntaxNode, ModuleRewrite>> Rewrites { get; } = rewrites;

    /// <summary>One call cut out of a chain the program keeps: written in the module as a statement of its own.</summary>
    public bool IsLink { get; init; }
}

/// <summary>What <see cref="ProgramConversion.Analyze"/> found in a document.</summary>
internal sealed class ConversionPlan
{
    /// <summary>Statements for the module's <c>ConfigureServices</c>, in order, locals among them.</summary>
    public List<MovedStatement> Registrations { get; } = [];

    /// <summary>Statements for the module's <c>UseEndpoints</c>, in order.</summary>
    public List<MovedStatement> Mappings { get; } = [];

    /// <summary>The nodes that go from the document.</summary>
    public List<SyntaxNode> Removed { get; } = [];

    /// <summary>Chains the program keeps without the calls that moved: each chain, and what replaces it.</summary>
    public Dictionary<ExpressionSyntax, ExpressionSyntax> Kept { get; } = [];

    /// <summary>
    /// The statement declaring the application when the program has no <c>UseRouting</c> and
    /// mappings move: the module's <c>UseEndpoints</c> needs routing in the host's pipeline, which
    /// <c>WebApplication</c> adds by itself only while it has endpoints of its own.
    /// </summary>
    public StatementSyntax? RoutingAfter { get; set; }

    /// <summary>The application local's name, for the <c>UseRouting</c> call.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// <c>UseModuleRouting(…)</c>, which also adds after routing what <c>WebApplication</c> adds
    /// implicitly and the program does not place itself, or <c>UseRouting()</c> when it places all of it.
    /// </summary>
    public string RoutingCall { get; set; } = "UseRouting()";

    /// <summary>The first statement that moves, where MOD0015 is reported.</summary>
    public SyntaxNode First => Registrations.Concat(Mappings).Select(statement => statement.Node).OrderBy(node => node.SpanStart).First();

    /// <summary>How many statements move.</summary>
    public int Count => Registrations.Count + Mappings.Count;
}
