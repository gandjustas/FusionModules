using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.Analyzers;

/// <summary>
/// What of a context's <c>OnModelCreating</c> is the configuration of one entity and can move into
/// an <c>IEntityTypeConfiguration&lt;T&gt;</c> class without the model changing (MOD0016). Read by
/// the analyzer, which reports it, and by the fix, which moves it.
/// </summary>
/// <remarks>
/// <para>
/// The method is read as the sequence of statements it runs: its own, and those of the context's
/// private helpers it hands the model builder to. A statement is the configuration of one entity
/// when it is <c>modelBuilder.Entity&lt;T&gt;(…)</c> with a lambda or a helper of the context, or a
/// chain of calls on <c>modelBuilder.Entity&lt;T&gt;()</c>. Everything else — the base call, a
/// default schema, a loop over the model's entity types, a condition — stays, and is something no
/// configuration is moved across: a loop over the entity types sees the entities configured before it.
/// </para>
/// <para>
/// A configuration class cannot see the context, so a statement that reads the context's instance
/// state, the model builder, a parameter or a local it does not own stays where it is. A tenant
/// filter that names a field of the context has to: EF Core evaluates it against the context of
/// each query, which a value handed to a constructor would not be.
/// </para>
/// </remarks>
internal static class EntityConfigurations
{
    public const string MethodName = "OnModelCreating";

    /// <summary>Whether the method could be a context's <c>OnModelCreating</c>, by its syntax alone.</summary>
    public static bool MightApply(MethodDeclarationSyntax method) =>
        method.Identifier.ValueText == MethodName &&
        method.ParameterList.Parameters.Count == 1 &&
        method.Modifiers.Any(SyntaxKind.OverrideKeyword);

    /// <summary>Whether the class could be a context with something to move, by its syntax alone.</summary>
    public static bool MightApply(ClassDeclarationSyntax type) =>
        type.Members.Any(member => member switch
        {
            MethodDeclarationSyntax method => MightApply(method),
            PropertyDeclarationSyntax property => IsDbSetName(property.Type),
            _ => false,
        });

    private static bool IsDbSetName(TypeSyntax type) => type switch
    {
        GenericNameSyntax { Identifier.ValueText: "DbSet" } => true,
        QualifiedNameSyntax { Right: GenericNameSyntax { Identifier.ValueText: "DbSet" } } => true,
        NullableTypeSyntax nullable => IsDbSetName(nullable.ElementType),
        _ => false,
    };

    /// <summary>The types the analysis asks about, and what it has to know of the whole compilation.</summary>
    internal sealed class Types
    {
        private readonly Compilation compilation;
        private readonly Lazy<Dictionary<ISymbol, List<INamedTypeSymbol>>> configurations;
        private readonly Lazy<bool> ownAssemblyIsModule;
        private readonly Lazy<bool> relational;

        private Types(Compilation compilation, INamedTypeSymbol modelBuilder, INamedTypeSymbol dbContext, INamedTypeSymbol configuration)
        {
            this.compilation = compilation;
            ModelBuilder = modelBuilder;
            DbContext = dbContext;
            Configuration = configuration;
            ModuleExtensions = compilation.GetTypeByMetadataName("FusionModules.EntityFrameworkCore.ModuleModelBuilderExtensions");
            DbSet = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
            relational = new Lazy<bool>(() => compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.RelationalEntityTypeBuilderExtensions") is not null);
            ownAssemblyIsModule = new Lazy<bool>(() => ModuleFacts.IsModuleAssembly(
                compilation.Assembly,
                compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName),
                ModuleFacts.GetModuleBaseType(compilation)));
            configurations = new Lazy<Dictionary<ISymbol, List<INamedTypeSymbol>>>(FindConfigurations);
        }

        public INamedTypeSymbol ModelBuilder { get; }

        public INamedTypeSymbol DbContext { get; }

        /// <summary><c>IEntityTypeConfiguration&lt;&gt;</c>.</summary>
        public INamedTypeSymbol Configuration { get; }

        /// <summary>The class of <c>ApplyModuleConfigurations</c>, where the project references FusionModules.EntityFrameworkCore.</summary>
        public INamedTypeSymbol? ModuleExtensions { get; }

        /// <summary><c>DbSet&lt;&gt;</c>.</summary>
        public INamedTypeSymbol? DbSet { get; }

        /// <summary>Whether the project maps to tables: <c>ToTable</c> is there to call.</summary>
        public bool Relational => relational.Value;

        /// <summary>Whether <c>ApplyModuleConfigurations</c> scans this assembly: it does a module's.</summary>
        public bool OwnAssemblyIsModule => ownAssemblyIsModule.Value;

        public static Types? For(Compilation compilation) =>
            compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ModelBuilder") is { } modelBuilder &&
            compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext") is { } dbContext &&
            compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1") is { } configuration
                ? new Types(compilation, modelBuilder, dbContext, configuration)
                : null;

        /// <summary>The compilation's own classes that configure the entity.</summary>
        public IReadOnlyList<INamedTypeSymbol> ConfigurationsOf(INamedTypeSymbol entity) =>
            configurations.Value.TryGetValue(entity, out var classes) ? classes : [];

        /// <summary>Every configuration class the compilation declares.</summary>
        public IEnumerable<INamedTypeSymbol> AllConfigurations => configurations.Value.Values.SelectMany(classes => classes).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        /// <summary>The entities a class configures.</summary>
        public IEnumerable<INamedTypeSymbol> EntitiesOf(INamedTypeSymbol type) =>
            type.AllInterfaces
                .Where(@interface => SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, Configuration))
                .Select(@interface => @interface.TypeArguments[0])
                .OfType<INamedTypeSymbol>();

        /// <summary>
        /// Whether another <c>OnModelCreating</c> of the compilation scans an assembly for
        /// configurations. It would find the classes a fix creates for this context, and its model
        /// would gain their entities. Read from the syntax: the question is asked once per context
        /// that has something to move.
        /// </summary>
        public bool ScansElsewhere(MethodDeclarationSyntax? self, CancellationToken cancellationToken)
        {
            foreach (var symbol in compilation.GetSymbolsWithName(MethodName, SymbolFilter.Member, cancellationToken))
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    if (self is not null && reference.SyntaxTree == self.SyntaxTree && reference.Span == self.Span)
                    {
                        continue;
                    }

                    foreach (var token in reference.GetSyntax(cancellationToken).DescendantTokens())
                    {
                        if (token.IsKind(SyntaxKind.IdentifierToken) &&
                            (token.ValueText == "ApplyConfigurationsFromAssembly" ||
                             (OwnAssemblyIsModule && token.ValueText == "ApplyModuleConfigurations")))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private Dictionary<ISymbol, List<INamedTypeSymbol>> FindConfigurations()
        {
            var found = new Dictionary<ISymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
            foreach (var symbol in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type))
            {
                if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type)
                {
                    continue;
                }

                foreach (var entity in EntitiesOf(type))
                {
                    if (!found.TryGetValue(entity, out var classes))
                    {
                        found.Add(entity, classes = []);
                    }

                    classes.Add(type);
                }
            }

            return found;
        }
    }

    internal enum ItemKind
    {
        /// <summary>Anything the analysis does not move, and moves nothing across.</summary>
        Barrier,

        /// <summary>
        /// A call that sets something of the model and reads none of its entity types: the base call of
        /// a plain context, <c>HasDefaultSchema</c>. Configuration is not moved across it either, but an
        /// entity may come into the model after it.
        /// </summary>
        Harmless,

        /// <summary>The configuration of one entity.</summary>
        Entity,

        /// <summary>A local variable's declaration.</summary>
        Local,

        /// <summary><c>ApplyConfiguration(new X())</c>.</summary>
        Apply,

        /// <summary>A scan that finds this assembly's configuration classes.</summary>
        Scan,

        /// <summary>A scan whose reach cannot be told: a predicate, an assembly not named by a type.</summary>
        UnknownScan,
    }

    internal enum PieceKind
    {
        /// <summary><c>modelBuilder.Entity&lt;T&gt;(e => …)</c>.</summary>
        Lambda,

        /// <summary><c>modelBuilder.Entity&lt;T&gt;().A().B()</c>.</summary>
        Chain,

        /// <summary>A helper of the context that takes the entity's builder.</summary>
        Helper,

        /// <summary>A local the moved statements use, declared for them alone.</summary>
        Local,
    }

    /// <summary>One statement of the sequence <c>OnModelCreating</c> runs.</summary>
    internal sealed class Item(SyntaxNode node, MethodDeclarationSyntax owner, string builder)
    {
        /// <summary>The statement, or the expression of an expression-bodied method.</summary>
        public SyntaxNode Node { get; } = node;

        /// <summary>The method it is written in: <c>OnModelCreating</c> or a helper.</summary>
        public MethodDeclarationSyntax Owner { get; } = owner;

        /// <summary>The model builder's name there.</summary>
        public string Builder { get; } = builder;

        public ItemKind Kind { get; set; }

        public Piece? Piece { get; set; }

        public ILocalSymbol? Local { get; set; }

        public INamedTypeSymbol? Applied { get; set; }

        public int Index { get; set; }
    }

    /// <summary>The code of one statement that would move.</summary>
    internal sealed class Piece(PieceKind kind, Item item, INamedTypeSymbol? entity, TypeSyntax? entityName, SyntaxNode scope)
    {
        public PieceKind Kind { get; } = kind;

        public Item Item { get; } = item;

        public INamedTypeSymbol? Entity { get; } = entity;

        /// <summary>The entity as the context names it.</summary>
        public TypeSyntax? EntityName { get; } = entityName;

        /// <summary>The node whose code moves: a lambda, a helper's body, the statement.</summary>
        public SyntaxNode Scope { get; } = scope;

        /// <summary>A chain's <c>modelBuilder.Entity&lt;T&gt;()</c>, which becomes the configuration's builder.</summary>
        public ExpressionSyntax? Root { get; set; }

        /// <summary>The entity's builder: a lambda's parameter, a helper's.</summary>
        public IParameterSymbol? Parameter { get; set; }

        /// <summary>The helper whose body moves, and which goes with it.</summary>
        public MethodDeclarationSyntax? Helper { get; set; }

        /// <summary>Why the code cannot be written in another class.</summary>
        public string? Blocked { get; set; }

        /// <summary>Why it stays, when it does: <see cref="Blocked"/>, or its order.</summary>
        public string? Left { get; set; }

        /// <summary>Where the builder's parameter is named.</summary>
        public List<TextSpan> ParameterUses { get; } = [];

        /// <summary>Where a static member or a nested type of the context is named without the context.</summary>
        public List<int> Qualify { get; } = [];

        /// <summary>The types the code names, and those of the members it reads.</summary>
        public HashSet<ISymbol> Mentions { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>The locals it reads that are declared outside it.</summary>
        public List<ILocalSymbol> Locals { get; } = [];

        /// <summary>Their declarations, when they are its entity's alone and move with it.</summary>
        public List<Item> Pulled { get; } = [];
    }

    /// <summary>A helper of the context that <c>OnModelCreating</c> hands the model builder to.</summary>
    internal sealed class HelperCall(MethodDeclarationSyntax helper, SyntaxNode call, List<Item> items)
    {
        public MethodDeclarationSyntax Helper { get; } = helper;

        /// <summary>The statement that calls it.</summary>
        public SyntaxNode Call { get; } = call;

        /// <summary>What it runs, nested helpers included.</summary>
        public List<Item> Items { get; } = items;
    }

    /// <summary>One entity's configuration, on its way to a class.</summary>
    internal sealed class Move(INamedTypeSymbol entity, List<Piece> pieces, INamedTypeSymbol? existing, bool apply)
    {
        public INamedTypeSymbol Entity { get; } = entity;

        /// <summary>The statements, in the order they run.</summary>
        public List<Piece> Pieces { get; } = pieces;

        /// <summary>The class that configures the entity already and is applied before them; null for a new one.</summary>
        public INamedTypeSymbol? Existing { get; } = existing;

        /// <summary>Whether the first statement becomes <c>ApplyConfiguration</c>: nothing scans the assembly.</summary>
        public bool Apply { get; } = apply;

        /// <summary>The table the entity's DbSet property named, to be said by the class once the property is gone.</summary>
        public string? Table { get; set; }
    }

    /// <summary>Where the entity of a DbSet property gets its place in the model once the property is gone.</summary>
    internal enum PropertyTarget
    {
        /// <summary>The class its configuration moves into.</summary>
        Move,

        /// <summary>A class the context applies already.</summary>
        Existing,

        /// <summary>A class of its own, created for it: nothing configured the entity but the property.</summary>
        New,
    }

    /// <summary>
    /// A <c>DbSet&lt;T&gt;</c> property of the context, on its way to an extension member of
    /// <c>DbContext</c>. The property did two things for the model — it put the entity in, from the
    /// start, and named its table — and both have to be said by a configuration class before it goes.
    /// </summary>
    internal sealed class PropertyPlan(PropertyDeclarationSyntax declaration, IPropertySymbol symbol, INamedTypeSymbol entity)
    {
        public PropertyDeclarationSyntax Declaration { get; } = declaration;

        public IPropertySymbol Symbol { get; } = symbol;

        public INamedTypeSymbol Entity { get; } = entity;

        public PropertyTarget Target { get; set; }

        public Move? Move { get; set; }

        public INamedTypeSymbol? Existing { get; set; }

        /// <summary>The table name a class has to say; null when the name does not change, or is said already.</summary>
        public string? Table { get; set; }

        /// <summary>Why the property stays; null when it can go.</summary>
        public string? Left { get; set; }
    }

    /// <summary>What a fix would do to one context.</summary>
    internal sealed class Plan(
        INamedTypeSymbol context, ClassDeclarationSyntax type, MethodDeclarationSyntax? method, List<Move> moves, List<PropertyPlan> properties,
        List<(string Entity, string Reason)> stays, List<HelperCall> helpers, bool scanned)
    {
        public INamedTypeSymbol Context { get; } = context;

        /// <summary>The declaration of the context that is read: the one with <c>OnModelCreating</c>, when there is one.</summary>
        public ClassDeclarationSyntax Type { get; } = type;

        /// <summary><c>OnModelCreating</c>; null for a context that has none, and only properties.</summary>
        public MethodDeclarationSyntax? Method { get; } = method;

        /// <summary>The DbSet properties, those that can go and those that stay.</summary>
        public List<PropertyPlan> Properties { get; } = properties;

        /// <summary>Whether the context finds its assembly's configuration classes itself.</summary>
        public bool Scanned { get; } = scanned;

        /// <summary>Where the diagnostic is: the method's name, or the context's.</summary>
        public SyntaxToken Identifier => Method?.Identifier ?? Type.Identifier;

        public int MovableProperties => Properties.Count(property => property.Left is null);

        public List<Move> Moves { get; } = moves;

        /// <summary>The entities with configuration that stays, and why.</summary>
        public List<(string Entity, string Reason)> Stays { get; } = stays;

        public List<HelperCall> Helpers { get; } = helpers;

        /// <summary>The second half of the message: what stays, shortened to what a line holds.</summary>
        public string StaysText()
        {
            if (Stays.Count == 0)
            {
                return string.Empty;
            }

            var shown = string.Join(", ", Stays.Take(3).Select(stay => $"{stay.Entity} {stay.Reason}"));
            return "; left in place: " + shown + (Stays.Count > 3 ? $" and {Stays.Count - 3} more" : string.Empty);
        }
    }

    private static class Reasons
    {
        public const string Order = "would run in another order against configuration that stays";
        public const string Nested = "is configured inside a larger statement";
        public const string ThroughLocal = "is configured through a local variable";
        public const string Chained = "is configured in a chain with other calls on the model builder";
        public const string Directives = "is among preprocessor directives";
        public const string This = "uses the context ('this')";
        public const string ModelBuilder = "uses the model builder";
        public const string BeforeExisting = "comes before its configuration class is applied";
        public const string NoBody = "has nothing to move";
        public const string Overridable = "is static or can be overridden";
        public const string Attributes = "has attributes";
        public const string NotPlain = "is more than the context's set of the entity";
        public const string Inline = "has configuration that stays in OnModelCreating and no class";
        public const string Registered = "would come into the model after a statement that may read its entity types";
        public const string ExpressionBody = "needs a statement in OnModelCreating, which has an expression body";
        public const string TwoSets = "is one of two sets of one entity";
        public const string NotRelational = "names its table, and the provider is not a relational one";

        public static string Implements(string member) => $"implements {member}";

        public static string Member(string name) => $"uses the context's '{name}'";

        public static string Parameter(string name) => $"uses the parameter '{name}'";

        public static string Local(string name) => $"uses the local '{name}'";

        public static string Unreachable(string name) => $"uses '{name}', which another class cannot name";

        public static string Existing(string name) => $"has '{name}', which this context does not apply in a way the fix can add to";
    }

    /// <summary>
    /// The plan for a context's <c>OnModelCreating</c>, or null when nothing of it moves.
    /// </summary>
    public static Plan? Analyze(SemanticModel model, ClassDeclarationSyntax type, Types types, CancellationToken cancellationToken)
    {
        if (model.GetDeclaredSymbol(type, cancellationToken) is not INamedTypeSymbol { ContainingType: null } context ||
            !Inherits(context, types.DbContext))
        {
            return null;
        }

        bool IsOnModelCreating(IMethodSymbol? method) =>
            method is { IsOverride: true, Parameters.Length: 1 } && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, types.ModelBuilder);

        var declaration = type.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => MightApply(method) && IsOnModelCreating(model.GetDeclaredSymbol(method, cancellationToken)));
        var analysis = new Analysis(model, types, context, cancellationToken);
        if (declaration is not null)
        {
            analysis.Flatten(declaration, model.GetDeclaredSymbol(declaration, cancellationToken)!.Parameters[0]);
        }
        else if (context.GetMembers(MethodName).OfType<IMethodSymbol>().Any(IsOnModelCreating) ||
                 FirstSet(context, types)?.DeclaringSyntaxReferences.FirstOrDefault() is not { } first ||
                 first.SyntaxTree != type.SyntaxTree || !type.Span.Contains(first.Span))
        {
            // A partial context is read where its OnModelCreating is; one without it, where its first set is.
            return null;
        }

        return analysis.Plan(type, declaration);
    }

    private static IPropertySymbol? FirstSet(INamedTypeSymbol context, Types types) =>
        context.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(property => SetOf(property, types) is not null);

    /// <summary>The entity of a <c>DbSet&lt;T&gt;</c> property.</summary>
    private static INamedTypeSymbol? SetOf(IPropertySymbol property, Types types) =>
        types.DbSet is not null &&
        property.Type is INamedTypeSymbol { TypeArguments.Length: 1 } set &&
        SymbolEqualityComparer.Default.Equals(set.OriginalDefinition, types.DbSet)
            ? set.TypeArguments[0] as INamedTypeSymbol
            : null;

    /// <summary>
    /// The <c>Configure</c> of a configuration class that statements can be added to: the class has
    /// one declaration, and the method a block.
    /// </summary>
    public static MethodDeclarationSyntax? ConfigureOf(INamedTypeSymbol type, INamedTypeSymbol entity, CancellationToken cancellationToken)
    {
        if (type.DeclaringSyntaxReferences.Length != 1)
        {
            return null;
        }

        var methods = type.GetMembers("Configure").OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 1 &&
                             method.Parameters[0].Type is INamedTypeSymbol { Name: "EntityTypeBuilder", TypeArguments: { Length: 1 } arguments } &&
                             SymbolEqualityComparer.Default.Equals(arguments[0], entity))
            .ToList();
        return methods.Count == 1 &&
               methods[0].DeclaringSyntaxReferences.Length == 1 &&
               methods[0].DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is MethodDeclarationSyntax { Body: not null } declaration &&
               !declaration.ContainsDirectives
            ? declaration
            : null;
    }

    private static bool Inherits(INamedTypeSymbol type, INamedTypeSymbol @base)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, @base))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a class anywhere in the project can name the type.</summary>
    private static bool Reachable(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal ||
                current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return type.TypeArguments.All(argument => argument is not INamedTypeSymbol named || Reachable(named));
    }

    private sealed class Analysis(SemanticModel model, Types types, INamedTypeSymbol context, CancellationToken cancellationToken)
    {
        private readonly List<Item> items = [];
        private readonly List<HelperCall> helpers = [];
        private readonly List<(INamedTypeSymbol Entity, string Reason)> noted = [];
        private readonly HashSet<MethodDeclarationSyntax> visited = [];

        public void Flatten(MethodDeclarationSyntax method, IParameterSymbol builder)
        {
            IEnumerable<SyntaxNode> nodes = method.Body is { } body
                ? body.Statements
                : method.ExpressionBody is { } arrow ? [arrow.Expression] : [];

            foreach (var node in nodes)
            {
                var expression = node as ExpressionSyntax ?? (node as ExpressionStatementSyntax)?.Expression;
                if (expression is InvocationExpressionSyntax call && ModelHelper(call, builder) is { } helper)
                {
                    var start = items.Count;
                    var entry = new HelperCall(helper.Declaration, node, []);
                    helpers.Add(entry);
                    Flatten(helper.Declaration, helper.Parameter);
                    entry.Items.AddRange(items.Skip(start));
                    continue;
                }

                var item = new Item(node, method, builder.Name) { Index = items.Count };
                items.Add(item);
                Classify(item, expression, builder);
            }
        }

        public Plan? Plan(ClassDeclarationSyntax type, MethodDeclarationSyntax? method)
        {
            if (items.Any(item => item.Kind == ItemKind.UnknownScan))
            {
                return null;
            }

            var entities = items
                .Where(item => item.Kind == ItemKind.Entity)
                .GroupBy(item => item.Piece!.Entity!, (IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default)
                .ToList();
            var properties = Properties();
            if (entities.Count == 0 && properties.All(property => property.Left is not null))
            {
                return null;
            }

            var scan = items.FindIndex(item => item.Kind == ItemKind.Scan);
            if (scan < 0 && types.ScansElsewhere(method, cancellationToken))
            {
                return null;
            }

            foreach (var item in items.Where(item => item.Kind == ItemKind.Entity && item.Piece!.Blocked is null))
            {
                PullLocals(item.Piece!);
            }

            var moves = new List<Move>();
            var stays = new List<(string Entity, string Reason)>();
            foreach (var group in entities)
            {
                var pieces = group.Select(item => item.Piece!).ToList();
                var move = Select(group.Key, pieces, scan);

                // A local that moved with one statement and is read by another that stays would be
                // declared in neither place: its statements stay together.
                while (move is not null && pieces.FirstOrDefault(piece =>
                           !move.Pieces.Contains(piece) && piece.Pulled.Any(local => move.Pieces.Any(moved => moved.Pulled.Contains(local)))) is { } torn)
                {
                    foreach (var piece in pieces.Where(piece => piece.Pulled.Intersect(torn.Pulled).Any()))
                    {
                        piece.Blocked ??= Reasons.Local(torn.Pulled[0].Local!.Name);
                    }

                    foreach (var piece in pieces)
                    {
                        piece.Left = null;
                    }

                    move = Select(group.Key, pieces, scan);
                }

                if (move is not null)
                {
                    moves.Add(move);
                }

                if (pieces.FirstOrDefault(piece => piece.Left is not null) is { } left)
                {
                    stays.Add((group.Key.Name + (move is null ? string.Empty : " in part"), left.Left!));
                }
            }

            foreach (var (entity, reason) in noted)
            {
                if (!stays.Any(stay => stay.Entity == entity.Name || stay.Entity == entity.Name + " in part"))
                {
                    stays.Add((entity.Name + (moves.Any(move => SymbolEqualityComparer.Default.Equals(move.Entity, entity)) ? " in part" : string.Empty), reason));
                }
            }

            Place(properties, moves, scan, method);
            stays.AddRange(properties.Where(property => property.Left is not null).Select(property => ("property " + property.Symbol.Name, property.Left!)));

            return moves.Count == 0 && properties.All(property => property.Left is not null)
                ? null
                : new Plan(context, type, method, moves, properties, stays, helpers, scan >= 0);
        }

        /// <summary>
        /// The context's DbSet properties, each with the reason it stays when its own shape gives one:
        /// what an extension member can stand in for is a plain property of the context — an
        /// auto-property or <c>=> Set&lt;T&gt;()</c> — that no interface asks for and nothing overrides.
        /// </summary>
        private List<PropertyPlan> Properties()
        {
            var properties = new List<PropertyPlan>();
            foreach (var property in context.GetMembers().OfType<IPropertySymbol>())
            {
                if (SetOf(property, types) is not { } entity ||
                    property.ExplicitInterfaceImplementations.Length > 0 ||
                    property.DeclaringSyntaxReferences.Length != 1 ||
                    property.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not PropertyDeclarationSyntax declaration)
                {
                    continue;
                }

                properties.Add(new PropertyPlan(declaration, property, entity) { Left = Shape(declaration, property, entity) });
            }

            foreach (var twice in properties.GroupBy(property => property.Entity, (IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default).Where(group => group.Count() > 1))
            {
                foreach (var property in twice)
                {
                    property.Left ??= Reasons.TwoSets;
                }
            }

            return properties;
        }

        private string? Shape(PropertyDeclarationSyntax declaration, IPropertySymbol property, INamedTypeSymbol entity)
        {
            if (property.IsStatic || property.IsVirtual || property.IsAbstract || property.IsOverride)
            {
                return Reasons.Overridable;
            }

            if (declaration.AttributeLists.Count > 0)
            {
                return Reasons.Attributes;
            }

            if (declaration.ContainsDirectives)
            {
                return Reasons.Directives;
            }

            if (!Reachable(entity) || entity.IsGenericType)
            {
                return Reasons.Unreachable(entity.Name);
            }

            foreach (var member in context.AllInterfaces.SelectMany(type => type.GetMembers(property.Name)))
            {
                if (SymbolEqualityComparer.Default.Equals(context.FindImplementationForInterfaceMember(member), property))
                {
                    return Reasons.Implements($"{member.ContainingType.Name}.{member.Name}");
                }
            }

            // { get; set; }, with nothing or "= null!" after it; or => Set<T>().
            var plain = declaration.ExpressionBody is { } arrow
                ? arrow.Expression is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call &&
                  call.Expression is GenericNameSyntax { Identifier.ValueText: "Set" }
                      or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: GenericNameSyntax { Identifier.ValueText: "Set" } }
                : declaration.AccessorList is { } accessors &&
                  accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null && accessor.AttributeLists.Count == 0) &&
                  (declaration.Initializer is null ||
                   declaration.Initializer.Value is PostfixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } suppressed && suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression));
            return plain ? null : Reasons.NotPlain;
        }

        /// <summary>
        /// Where each property's entity gets its place in the model without the property, and whether
        /// that is as good a place. The property put the entity in before <c>OnModelCreating</c> ran;
        /// a class puts it in where the class is applied — or, for an entity with no configuration at
        /// all, as the method's first statement — so nothing before that may read the model's entity
        /// types. And the property named the table, which the class then has to.
        /// </summary>
        private void Place(List<PropertyPlan> properties, List<Move> moves, int scan, MethodDeclarationSyntax? method)
        {
            foreach (var property in properties.Where(property => property.Left is null))
            {
                var entity = property.Entity;
                var pieces = items.Where(item => item.Kind == ItemKind.Entity && SymbolEqualityComparer.Default.Equals(item.Piece!.Entity, entity)).ToList();
                var existing = types.ConfigurationsOf(entity);
                var applied = items
                    .Where(item => item.Kind == ItemKind.Apply && types.EntitiesOf(item.Applied!).Contains(entity, SymbolEqualityComparer.Default))
                    .ToList();
                var classes = existing.Concat(applied.Select(item => item.Applied!)).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();

                var at = pieces.Select(item => item.Index).Concat(applied.Select(item => item.Index)).DefaultIfEmpty(int.MaxValue).Min();
                if (moves.FirstOrDefault(move => SymbolEqualityComparer.Default.Equals(move.Entity, entity)) is { } move)
                {
                    property.Target = PropertyTarget.Move;
                    property.Move = move;
                    property.Existing = move.Existing;
                    at = scan >= 0 ? Math.Min(at, scan) : at;
                }
                else if (pieces.Count > 0 && classes.Count == 0)
                {
                    property.Left = Reasons.Inline;
                }
                else if (classes.Count == 0)
                {
                    property.Target = PropertyTarget.New;
                    at = scan >= 0 ? scan : -1;
                    if (scan < 0 && method is { Body: null })
                    {
                        property.Left = Reasons.ExpressionBody;
                    }
                }
                else if (classes.Count == 1 && (applied.Count > 0 || (scan >= 0 && IsScanned(classes[0]))))
                {
                    property.Target = PropertyTarget.Existing;
                    property.Existing = classes[0];
                    at = applied.Count == 0 ? Math.Min(at, scan) : at;
                }
                else
                {
                    property.Left = Reasons.Existing(classes[0].Name);
                }

                if (property.Left is null && items.Take(Math.Max(at, 0)).Any(item => item.Kind == ItemKind.Barrier))
                {
                    property.Left = Reasons.Registered;
                }

                if (property.Left is null && property.Symbol.Name != entity.Name && !NamesItsTable(entity, pieces, classes, properties))
                {
                    // The class says what the property said. A class that is not source, or has no block to add to, cannot.
                    if (!types.Relational)
                    {
                        property.Left = Reasons.NotRelational;
                    }
                    else if (property.Existing is { } target && !CanTakeMore(target, entity))
                    {
                        property.Left = Reasons.Existing(target.Name);
                    }
                    else
                    {
                        property.Table = property.Symbol.Name;
                        if (property.Move is not null)
                        {
                            property.Move.Table = property.Table;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Whether the entity's table is named without its DbSet property: by a call in its own
        /// configuration, by an attribute, or by the entity it derives from, whose table it shares
        /// unless the hierarchy is mapped a table per type.
        /// </summary>
        private bool NamesItsTable(INamedTypeSymbol entity, List<Item> pieces, List<INamedTypeSymbol> classes, List<PropertyPlan> properties)
        {
            if (entity.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "TableAttribute"))
            {
                return true;
            }

            var named = new HashSet<string>(
                pieces.SelectMany(item => (item.Piece!.Helper ?? item.Node).DescendantTokens().Concat(item.Node.DescendantTokens()))
                    .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
                    .Select(token => token.ValueText),
                StringComparer.Ordinal);
            foreach (var type in classes)
            {
                if (type.DeclaringSyntaxReferences.Length == 0)
                {
                    // Not source: what it says of the table is not known, and nothing can be added to it.
                    return false;
                }

                named.UnionWith(Names(type));
            }

            if (TableCalls.Any(named.Contains))
            {
                return true;
            }

            for (var parent = entity.BaseType; parent is not null; parent = parent.BaseType)
            {
                if (properties.Any(property => SymbolEqualityComparer.Default.Equals(property.Entity, parent)) ||
                    items.Any(item => item.Kind == ItemKind.Entity && SymbolEqualityComparer.Default.Equals(item.Piece!.Entity, parent)) ||
                    types.ConfigurationsOf(parent).Count > 0)
                {
                    // One table for the hierarchy, unless it is mapped a table per type: then each type's is its own.
                    return !items.SelectMany(item => item.Node.DescendantTokens()).Any(token => token.ValueText is "UseTpcMappingStrategy" or "UseTptMappingStrategy") &&
                           !types.ConfigurationsOf(parent).SelectMany(Names).Any(name => name is "UseTpcMappingStrategy" or "UseTptMappingStrategy");
                }
            }

            return false;
        }

        private static readonly string[] TableCalls = ["ToTable", "ToView", "ToSqlQuery", "ToFunction", "ToContainer", "ToCollection"];

        /// <summary>
        /// Which of an entity's statements move, and where to. With a scan in the method the class is
        /// applied where the scan is; with a class of its own applied before them, where that is;
        /// otherwise where the first of them stands. A statement moves when nothing between it and
        /// that place depends on the order: no statement that stays for a reason of its own, and no
        /// other entity's configuration that names this entity, or is named by it.
        /// </summary>
        private Move? Select(INamedTypeSymbol entity, List<Piece> pieces, int scan)
        {
            var existing = types.ConfigurationsOf(entity);
            var applied = items
                .Where(item => item.Kind == ItemKind.Apply && types.EntitiesOf(item.Applied!).Contains(entity, SymbolEqualityComparer.Default))
                .ToList();

            var mentions = new HashSet<ISymbol>(pieces.SelectMany(piece => piece.Mentions), SymbolEqualityComparer.Default);
            var moved = new List<Piece>();
            INamedTypeSymbol? target = null;
            HashSet<string> named = [];

            if (existing.Count == 0 && applied.Count == 0)
            {
                if (scan >= 0)
                {
                    Walk(scan + 1, 1);
                    Walk(scan - 1, -1);
                }
                else if (pieces.FirstOrDefault(piece => piece.Blocked is null) is { } first)
                {
                    Walk(first.Item.Index, 1);
                }
            }
            else if (existing.Count == 1 && CanTakeMore(existing[0], entity) &&
                     (scan >= 0
                         ? applied.Count == 0 && IsScanned(existing[0])
                         : applied.Count == 1 && SymbolEqualityComparer.Default.Equals(applied[0].Applied, existing[0])))
            {
                // What the class says already is the entity's configuration too: the types it names
                // are the entity's to keep its order against.
                target = existing[0];
                named = Names(target);
                var at = scan >= 0 ? scan : applied[0].Index;
                foreach (var piece in pieces.Where(piece => piece.Item.Index < at))
                {
                    piece.Left = piece.Blocked ?? Reasons.BeforeExisting;
                }

                Walk(at + 1, 1);
            }
            else
            {
                var name = existing.Count > 0 ? existing[0].Name : applied[0].Applied!.Name;
                foreach (var piece in pieces)
                {
                    piece.Left = piece.Blocked ?? Reasons.Existing(name);
                }
            }

            foreach (var piece in pieces.Where(piece => piece.Left is null && !moved.Contains(piece)))
            {
                piece.Left = piece.Blocked ?? Reasons.Order;
            }

            return moved.Count == 0
                ? null
                : new Move(entity, [.. moved.OrderBy(piece => piece.Item.Index)], target, apply: scan < 0 && target is null);

            void Walk(int from, int step)
            {
                var stopped = false;
                for (var i = from; i >= 0 && i < items.Count; i += step)
                {
                    var item = items[i];
                    if (item.Kind == ItemKind.Entity && SymbolEqualityComparer.Default.Equals(item.Piece!.Entity, entity))
                    {
                        if (!stopped && item.Piece.Blocked is null)
                        {
                            moved.Add(item.Piece);
                        }
                        else
                        {
                            item.Piece.Left = item.Piece.Blocked ?? Reasons.Order;
                            stopped = true;
                        }
                    }
                    else if (item.Kind == ItemKind.Entity)
                    {
                        stopped |= item.Piece!.Mentions.Contains(entity) || mentions.Contains(item.Piece.Entity!) ||
                            named.Contains(item.Piece.Entity!.Name) ||
                            Inherits(entity, item.Piece.Entity!) || Inherits(item.Piece.Entity!, entity);
                    }
                    else if (item.Kind == ItemKind.Apply)
                    {
                        stopped |= !Apart(item.Applied!, entity, mentions, named);
                    }
                    else if (item.Kind != ItemKind.Local || item.Piece?.Blocked is not null)
                    {
                        stopped = true;
                    }
                }
            }
        }

        /// <summary>
        /// Whether a configuration class applied between an entity's statements has nothing to do with
        /// the entity: it configures another, which the entity's statements do not name, and its
        /// source does not name the entity. A class that is not source is not known to.
        /// </summary>
        private bool Apart(INamedTypeSymbol applied, INamedTypeSymbol entity, HashSet<ISymbol> mentions, HashSet<string> named)
        {
            if (applied.DeclaringSyntaxReferences.Length == 0)
            {
                return false;
            }

            foreach (var other in types.EntitiesOf(applied))
            {
                if (SymbolEqualityComparer.Default.Equals(other, entity) || mentions.Contains(other) || named.Contains(other.Name) ||
                    Inherits(entity, other) || Inherits(other, entity))
                {
                    return false;
                }
            }

            return !Names(applied).Contains(entity.Name);
        }

        /// <summary>Every identifier a class's source has: what it may name, read without binding it.</summary>
        private HashSet<string> Names(INamedTypeSymbol type) =>
            new(
                type.DeclaringSyntaxReferences
                    .SelectMany(reference => reference.GetSyntax(cancellationToken).DescendantTokens())
                    .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
                    .Select(token => token.ValueText),
                StringComparer.Ordinal);

        /// <summary>Whether statements can be added to the class: one declaration with a <c>Configure</c> that has a block.</summary>
        private bool CanTakeMore(INamedTypeSymbol type, INamedTypeSymbol entity) =>
            ConfigureOf(type, entity, cancellationToken) is not null;

        /// <summary>Whether EF Core's assembly scan applies the class: it needs a public constructor without parameters.</summary>
        private static bool IsScanned(INamedTypeSymbol type) =>
            !type.IsAbstract && !type.IsGenericType &&
            type.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public);

        /// <summary>
        /// A local the statement reads moves with it when it is this entity's alone — declared in the
        /// same method, from an expression that needs nothing of the context, and read by this
        /// entity's statements only. A seed list handed to <c>HasData</c> is the usual one.
        /// </summary>
        private void PullLocals(Piece piece)
        {
            foreach (var local in piece.Locals)
            {
                var declaration = items.FirstOrDefault(item => item.Kind == ItemKind.Local && SymbolEqualityComparer.Default.Equals(item.Local, local));
                if (declaration?.Piece is not { Blocked: null, Locals.Count: 0 } ||
                    declaration.Owner != piece.Item.Owner ||
                    declaration.Index > piece.Item.Index ||
                    !ReadOnlyBy(declaration, piece.Entity!))
                {
                    piece.Blocked = Reasons.Local(local.Name);
                    return;
                }

                piece.Pulled.Add(declaration);
            }
        }

        private bool ReadOnlyBy(Item declaration, INamedTypeSymbol entity)
        {
            var name = declaration.Local!.Name;
            foreach (var use in declaration.Owner.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (use.Identifier.ValueText != name || declaration.Node.Span.Contains(use.Span))
                {
                    continue;
                }

                var user = items.FirstOrDefault(item => item.Owner == declaration.Owner && item.Node.Span.Contains(use.Span));
                if (user is not { Kind: ItemKind.Entity } || !SymbolEqualityComparer.Default.Equals(user.Piece!.Entity, entity))
                {
                    return false;
                }
            }

            return true;
        }

        private void Classify(Item item, ExpressionSyntax? expression, IParameterSymbol builder)
        {
            if (item.Node is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: 0, Declaration.Variables: { Count: 1 } variables } &&
                variables[0].Initializer is { } initializer &&
                model.GetDeclaredSymbol(variables[0], cancellationToken) is ILocalSymbol local)
            {
                if (EntityRoot(initializer.Value, builder) is { } rooted)
                {
                    noted.Add((rooted, Reasons.ThroughLocal));
                    return;
                }

                item.Kind = ItemKind.Local;
                item.Local = local;
                item.Piece = new Piece(PieceKind.Local, item, null, null, item.Node);
                Inspect(item.Piece, builder);
                return;
            }

            if (expression is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax access && IsParameter(access.Expression, builder))
                {
                    var arguments = invocation.ArgumentList.Arguments;
                    switch (access.Name.Identifier.ValueText)
                    {
                        case "ApplyConfiguration":
                            if (arguments.Count == 1 &&
                                arguments[0].Expression is ObjectCreationExpressionSyntax { Initializer: null } creation &&
                                (creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0) &&
                                model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol applied)
                            {
                                item.Kind = ItemKind.Apply;
                                item.Applied = applied;
                            }

                            return;

                        case "ApplyConfigurationsFromAssembly":
                            item.Kind = arguments.Count == 0 ? ItemKind.Barrier : ScanOf(arguments[0].Expression, arguments.Count);
                            return;

                        case "ApplyModuleConfigurations":
                            if (types.ModuleExtensions is not null &&
                                model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol composed &&
                                SymbolEqualityComparer.Default.Equals(composed.ContainingType, types.ModuleExtensions) &&
                                types.OwnAssemblyIsModule)
                            {
                                item.Kind = ItemKind.Scan;
                            }

                            return;
                    }
                }

                if (EntityPiece(item, invocation, builder) is { } piece)
                {
                    item.Kind = ItemKind.Entity;
                    item.Piece = piece;
                    return;
                }

                if (IsModelCall(invocation, builder))
                {
                    item.Kind = ItemKind.Harmless;
                    return;
                }
            }

            // What stays configures entities too, and the message says which.
            foreach (var name in item.Node.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
            {
                if (name.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax nested } &&
                    EntityCall(nested, builder, out _, out var entity) &&
                    !noted.Any(note => SymbolEqualityComparer.Default.Equals(note.Entity, entity)))
                {
                    noted.Add((entity, Reasons.Nested));
                }
            }
        }

        /// <summary>
        /// A statement that sets something of the model without reading its entity types: the base call
        /// of a context that derives from <c>DbContext</c> itself, which does nothing; or a call on the
        /// model builder that EF Core or a provider declares — <c>HasDefaultSchema</c>,
        /// <c>HasPostgresExtension</c>, <c>HasSequence(…).StartsAt(…)</c>. An extension of anybody
        /// else's may add entities or walk the model.
        /// </summary>
        private bool IsModelCall(InvocationExpressionSyntax invocation, IParameterSymbol builder)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax, Name.Identifier.ValueText: MethodName })
            {
                return SymbolEqualityComparer.Default.Equals(context.BaseType, types.DbContext);
            }

            for (ExpressionSyntax? current = invocation; current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call; current = member.Expression)
            {
                if (IsParameter(member.Expression, builder))
                {
                    return model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol method &&
                           method.Name is not ("Entity" or "SharedTypeEntity") &&
                           !method.Name.StartsWith("Apply", StringComparison.Ordinal) &&
                           (method.ReducedFrom ?? method).ContainingAssembly.Name.Contains("EntityFrameworkCore");
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a scan finds this assembly's classes. One named by a type of another assembly does
        /// not; one whose assembly is not named by a type, or that filters by a predicate, may.
        /// </summary>
        private ItemKind ScanOf(ExpressionSyntax assembly, int arguments)
        {
            var own = assembly switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assembly", Expression: TypeOfExpressionSyntax typeOf } =>
                    model.GetTypeInfo(typeOf.Type, cancellationToken).Type is { } type
                        ? SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, model.Compilation.Assembly)
                        : (bool?)null,
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assembly", Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "GetType" } or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "GetType" } } } => true,
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetExecutingAssembly" } } => true,
                _ => null,
            };

            return own switch
            {
                false => ItemKind.Barrier,
                true when arguments == 1 => ItemKind.Scan,
                _ => ItemKind.UnknownScan,
            };
        }

        private Piece? EntityPiece(Item item, InvocationExpressionSyntax invocation, IParameterSymbol builder)
        {
            Piece? piece = null;
            var arguments = invocation.ArgumentList.Arguments;
            if (EntityCall(invocation, builder, out var typeName, out var entity))
            {
                if (arguments.Count != 1)
                {
                    noted.Add((entity, Reasons.NoBody));
                    return null;
                }

                var argument = arguments[0].Expression;
                if (argument is LambdaExpressionSyntax lambda && LambdaParameter(lambda) is { } parameter)
                {
                    piece = new Piece(PieceKind.Lambda, item, entity, typeName, lambda)
                    {
                        Parameter = model.GetDeclaredSymbol(parameter, cancellationToken),
                    };
                }
                else if (EntityHelper(argument) is { } group)
                {
                    piece = new Piece(PieceKind.Helper, item, entity, typeName, group.Body) { Parameter = group.Parameter, Helper = group.Declaration };
                }
                else
                {
                    noted.Add((entity, Reasons.Nested));
                    return null;
                }
            }
            else if (arguments.Count == 1 &&
                     arguments[0].Expression is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } handed &&
                     EntityCall(handed, builder, out typeName, out entity) &&
                     EntityHelper(invocation.Expression) is { } called)
            {
                piece = new Piece(PieceKind.Helper, item, entity, typeName, called.Body) { Parameter = called.Parameter, Helper = called.Declaration };
            }
            else
            {
                ExpressionSyntax current = invocation;
                while (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member })
                {
                    if (member.Expression is InvocationExpressionSyntax root && EntityCall(root, builder, out typeName, out entity))
                    {
                        if (root.ArgumentList.Arguments.Count != 0)
                        {
                            noted.Add((entity, Reasons.Chained));
                            return null;
                        }

                        piece = new Piece(PieceKind.Chain, item, entity, typeName, item.Node) { Root = root };
                        break;
                    }

                    current = member.Expression;
                }
            }

            if (piece is null)
            {
                return null;
            }

            if (!Reachable(piece.Entity!))
            {
                piece.Blocked = Reasons.Unreachable(piece.Entity!.Name);
            }
            else if (item.Node.ContainsDirectives ||
                     item.Node.GetLastToken().GetNextToken().LeadingTrivia.Any(trivia => trivia.IsDirective))
            {
                piece.Blocked = Reasons.Directives;
            }

            Inspect(piece, builder);
            return piece;
        }

        private static ParameterSyntax? LambdaParameter(LambdaExpressionSyntax lambda) => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: { Count: 1 } parameters } => parameters[0],
            _ => null,
        };

        /// <summary>The entity a local is the builder of: <c>var order = modelBuilder.Entity&lt;Order&gt;()</c>, however long the chain after it.</summary>
        private INamedTypeSymbol? EntityRoot(ExpressionSyntax expression, IParameterSymbol builder)
        {
            for (var current = expression; current is InvocationExpressionSyntax invocation;)
            {
                if (EntityCall(invocation, builder, out _, out var entity))
                {
                    return entity;
                }

                current = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            }

            return null;
        }

        /// <summary><c>modelBuilder.Entity&lt;T&gt;(…)</c> on the model builder itself.</summary>
        private bool EntityCall(InvocationExpressionSyntax invocation, IParameterSymbol builder, out TypeSyntax typeName, out INamedTypeSymbol entity)
        {
            typeName = null!;
            entity = null!;
            if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Entity", TypeArgumentList.Arguments: { Count: 1 } typeArguments } } access &&
                IsParameter(access.Expression, builder) &&
                model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
                SymbolEqualityComparer.Default.Equals(method.ContainingType, types.ModelBuilder) &&
                model.GetTypeInfo(typeArguments[0], cancellationToken).Type is INamedTypeSymbol named)
            {
                typeName = typeArguments[0];
                entity = named;
                return true;
            }

            return false;
        }

        private bool IsParameter(ExpressionSyntax expression, IParameterSymbol parameter) =>
            expression is IdentifierNameSyntax identifier &&
            identifier.Identifier.ValueText == parameter.Name &&
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameter);

        /// <summary>A private method of the context that the model builder is handed to, once.</summary>
        private (MethodDeclarationSyntax Declaration, IParameterSymbol Parameter)? ModelHelper(InvocationExpressionSyntax call, IParameterSymbol builder)
        {
            if (call.ArgumentList.Arguments.Count != 1 ||
                !IsParameter(call.ArgumentList.Arguments[0].Expression, builder) ||
                OwnHelper(call.Expression) is not { } helper ||
                !SymbolEqualityComparer.Default.Equals(helper.Parameter.Type, types.ModelBuilder) ||
                !visited.Add(helper.Declaration))
            {
                return null;
            }

            return (helper.Declaration, helper.Parameter);
        }

        /// <summary>A private method of the context that takes an entity's builder, named once.</summary>
        private (MethodDeclarationSyntax Declaration, SyntaxNode Body, IParameterSymbol Parameter)? EntityHelper(ExpressionSyntax name)
        {
            if (OwnHelper(name) is not { } helper ||
                helper.Parameter.Type is not INamedTypeSymbol { Name: "EntityTypeBuilder", Arity: 1 } ||
                !visited.Add(helper.Declaration))
            {
                return null;
            }

            return (helper.Declaration, (SyntaxNode?)helper.Declaration.Body ?? helper.Declaration.ExpressionBody!.Expression, helper.Parameter);
        }

        private (MethodDeclarationSyntax Declaration, IParameterSymbol Parameter)? OwnHelper(ExpressionSyntax name)
        {
            if (name is not (IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }) ||
                model.GetSymbolInfo(name, cancellationToken).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                    DeclaredAccessibility: Accessibility.Private,
                    IsGenericMethod: false,
                    ReturnsVoid: true,
                    Parameters.Length: 1,
                    DeclaringSyntaxReferences.Length: 1,
                } method ||
                !SymbolEqualityComparer.Default.Equals(method.ContainingType, context) ||
                method.DeclaringSyntaxReferences[0].SyntaxTree != model.SyntaxTree ||
                method.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not MethodDeclarationSyntax declaration ||
                (declaration.Body is null && declaration.ExpressionBody is null) ||
                declaration.ContainsDirectives)
            {
                return null;
            }

            // Named once in the whole context, every part of a partial one: the call being read.
            var uses = context.DeclaringSyntaxReferences
                .SelectMany(reference => reference.GetSyntax(cancellationToken).DescendantNodes().OfType<IdentifierNameSyntax>())
                .Count(identifier => identifier.Identifier.ValueText == method.Name);
            return uses == 1 ? (declaration, method.Parameters[0]) : null;
        }

        /// <summary>
        /// Reads the code that would move for what it needs of the place it is in: the context, the
        /// model builder, parameters and locals. A static member or a nested type of the context that
        /// another class can name is noted, to be written with the context's name.
        /// </summary>
        private void Inspect(Piece piece, IParameterSymbol builder)
        {
            var scope = piece.Scope;
            foreach (var node in scope.DescendantNodesAndSelf())
            {
                if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                {
                    piece.Blocked ??= Reasons.This;
                    continue;
                }

                if (node is not SimpleNameSyntax name || (piece.Root is not null && piece.Root.Span.Contains(name.Span)))
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                if (symbol is null)
                {
                    continue;
                }

                switch (symbol)
                {
                    case INamedTypeSymbol type:
                        Mention(piece, type);
                        if (!Reachable(type))
                        {
                            piece.Blocked ??= Reasons.Unreachable(type.Name);
                        }

                        break;
                    case IPropertySymbol property:
                        Mention(piece, property.Type);
                        break;
                    case IFieldSymbol field:
                        Mention(piece, field.Type);
                        break;
                    case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type }:
                        piece.Blocked ??= Reasons.Unreachable(symbol.Name);
                        break;
                }

                var qualified = name.Parent switch
                {
                    MemberAccessExpressionSyntax access => access.Name == name,
                    QualifiedNameSyntax qualifiedName => qualifiedName.Right == name,
                    MemberBindingExpressionSyntax => true,
                    AliasQualifiedNameSyntax => true,
                    NameColonSyntax or NameEqualsSyntax => true,
                    _ => false,
                };
                if (qualified)
                {
                    continue;
                }

                switch (symbol)
                {
                    case ILocalSymbol local when Outside(local, scope):
                        if (!piece.Locals.Contains(local, SymbolEqualityComparer.Default))
                        {
                            piece.Locals.Add(local);
                        }

                        break;
                    case IParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(parameter, piece.Parameter):
                        piece.ParameterUses.Add(name.Span);
                        break;
                    case IParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(parameter, builder):
                        piece.Blocked ??= Reasons.ModelBuilder;
                        break;
                    case IParameterSymbol parameter when Outside(parameter, scope):
                        piece.Blocked ??= Reasons.Parameter(parameter.Name);
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.LocalFunction } function when Outside(function, scope):
                        piece.Blocked ??= Reasons.Local(function.Name);
                        break;
                    case IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol or INamedTypeSymbol when OfContext(symbol.ContainingType):
                        // An object initializer names the members of what it creates, not of the context.
                        if (name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment && assignment.Left == name)
                        {
                            break;
                        }

                        if (!symbol.IsStatic && symbol is not INamedTypeSymbol)
                        {
                            piece.Blocked ??= Reasons.Member(symbol.Name);
                        }
                        else if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                        {
                            piece.Blocked ??= Reasons.Member(symbol.Name);
                        }
                        else
                        {
                            piece.Qualify.Add(name.SpanStart);
                        }

                        break;
                }
            }
        }

        private bool OfContext(INamedTypeSymbol? containing)
        {
            for (var current = context; current is not null && containing is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, containing.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Outside(ISymbol symbol, SyntaxNode scope) =>
            symbol.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference ||
            reference.SyntaxTree != scope.SyntaxTree ||
            !scope.Span.Contains(reference.Span);

        private static void Mention(Piece piece, ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                Mention(piece, array.ElementType);
            }
            else if (type is INamedTypeSymbol named && piece.Mentions.Add(named.OriginalDefinition))
            {
                piece.Mentions.Add(named);
                foreach (var argument in named.TypeArguments)
                {
                    Mention(piece, argument);
                }
            }
        }
    }
}
