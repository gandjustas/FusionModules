using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionModules.Analyzers;

/// <summary>
/// A context whose <c>OnModelCreating</c> configures entities that could each have an
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> class, or whose <c>DbSet</c> properties could be
/// extension members of <c>DbContext</c> (MOD0016).
/// </summary>
/// <remarks>
/// <para>
/// Not a defect: a pointer to the split, carried by a diagnostic so that its fix runs through
/// <c>dotnet format</c>. A model composed from modules is made of configuration classes —
/// <c>ApplyModuleConfigurations</c> finds them in each loaded module — so configuration written in
/// the context is what keeps an entity from moving into its module.
/// </para>
/// <para>
/// Reported on <c>OnModelCreating</c> — on the context's name when it has none — once per context,
/// and only when something can move without the model changing; the message names what stays and
/// why. Nothing is read but classes that declare an <c>OnModelCreating</c> or a <c>DbSet</c>
/// property, and nothing at all in a project without EF Core. How a property is used is not read
/// here — that would bind the whole project — but by the fix, which leaves a property that is
/// assigned or named by <c>nameof</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EntityConfigurationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.EntityConfigurationCanMove];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (EntityConfigurations.Types.For(context.Compilation) is not { } types)
        {
            return;
        }

        context.RegisterSyntaxNodeAction(
            nodeContext =>
            {
                var type = (ClassDeclarationSyntax)nodeContext.Node;
                if (EntityConfigurations.MightApply(type) &&
                    EntityConfigurations.Analyze(nodeContext.SemanticModel, type, types, nodeContext.CancellationToken) is { } plan)
                {
                    nodeContext.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.EntityConfigurationCanMove,
                        plan.Identifier.GetLocation(),
                        plan.Context.Name,
                        plan.Moves.Count,
                        plan.MovableProperties,
                        plan.StaysText()));
                }
            },
            SyntaxKind.ClassDeclaration);
    }
}
