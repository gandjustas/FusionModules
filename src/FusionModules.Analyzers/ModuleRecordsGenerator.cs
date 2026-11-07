using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using static FusionModules.Analyzers.ModuleRecords;

namespace FusionModules.Analyzers;

/// <summary>
/// Records, in a module's own assembly, what the host's build needs to know and cannot read from the
/// module's reference assembly: the services the module's code registers (MOD0014).
/// <see cref="ModuleRecords"/> reads them back.
/// </summary>
/// <remarks>
/// <para>
/// The host's build sees the module as metadata, and a reference assembly at that: no method bodies,
/// so no trace of a call to <c>AddSingleton</c>. So the module writes its registrations down as
/// internal assembly attributes, which reference assemblies keep. Internal on purpose: nothing new
/// appears in the module's surface, MOD0001 has nothing to say, and an analyzer reads internal
/// attributes of a reference as readily as public ones.
/// </para>
/// <para>
/// One namespace per assembly, followed by the module's assembly name, so that a module which sees
/// another one's internals is not handed two declarations of one type (CS0436).
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed partial class ModuleRecordsGenerator : IIncrementalGenerator
{
    private const string HintName = "FusionModules.ModuleRecords.g.cs";

    /// <summary>The namespace the attributes are declared under, followed by the module's assembly name.</summary>
    internal const string AttributeNamespace = "FusionModules.Generated";

    /// <summary>The attribute that records a registered service.</summary>
    internal const string RegistrationAttributeName = "ModuleRegistrationAttribute";

    /// <summary>Separates the service's spellings in the registration's last argument.</summary>
    internal const char SpellingSeparator = '|';

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var kind = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ModuleFacts.GetProjectKind(provider.GlobalOptions))
            .WithTrackingName(TrackingNames.ProjectKind);

        // The assembly's name when it is a module, null otherwise: a string, so that an edit that
        // leaves the answer alone stops the pipeline here.
        var module = context.CompilationProvider
            .Combine(kind)
            .Select(static (input, token) => ModuleName(input.Left, input.Right, token))
            .WithTrackingName(TrackingNames.Module);

        var registrations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsRegistrationCandidate(node),
                static (syntax, token) => ReadRegistration(syntax, token))
            .WithTrackingName(TrackingNames.RegistrationCandidates)
            .Where(static registration => registration is not null)
            .Select(static (registration, _) => registration!.Value)
            .Collect()

            // Sorted and de-duplicated here, so that calls reordered or repeated leave the value alone.
            .Select(static (found, _) => new EquatableArray<Registration>(found
                .Distinct()
                .OrderBy(registration => registration.Service, StringComparer.Ordinal)
                .ThenBy(registration => registration.ServiceIdentity, StringComparer.Ordinal)
                .ThenBy(registration => registration.ImplementationIdentity, StringComparer.Ordinal)))
            .WithTrackingName(TrackingNames.Registrations);

        context.RegisterSourceOutput(registrations.Combine(module), static (production, input) =>
        {
            var (registered, assemblyName) = input;
            if (assemblyName is null || registered.IsEmpty)
            {
                return;
            }

            production.AddSource(HintName, SourceText.From(Render(assemblyName, registered), Encoding.UTF8));
        });
    }

    /// <summary>
    /// The assembly's name when it is a module, null otherwise. A host or a test project is not
    /// one, whatever it declares: MSBuild classifies the project, and without the package's targets
    /// an entry point is what makes a host (<see cref="ModuleFacts.IsHost(ProjectKind, Compilation, CancellationToken)"/>).
    /// </summary>
    private static string? ModuleName(Compilation compilation, ProjectKind kind, CancellationToken token)
    {
        if (kind == ProjectKind.Test || ModuleFacts.IsHost(kind, compilation, token))
        {
            return null;
        }

        var hostingStartup = compilation.GetTypeByMetadataName(ModuleFacts.HostingStartupAttributeMetadataName);
        var moduleBase = ModuleFacts.GetModuleBaseType(compilation);
        return ModuleFacts.IsModuleAssembly(compilation.Assembly, hostingStartup, moduleBase) ? compilation.AssemblyName : null;
    }

    private static string Render(string assemblyName, EquatableArray<Registration> registrations)
    {
        var ns = $"global::{AttributeNamespace}.{Identifier(assemblyName)}";
        var lines = registrations.Select(registration => $"[assembly: {ns}.{RegistrationAttributeName}({registration.Arguments()})]");

        return $$"""
            // <auto-generated/>

            {{string.Join("\n", lines)}}

            namespace {{AttributeNamespace}}.{{Identifier(assemblyName)}}
            {
                /// <summary>
                /// A service this module's code registers, recorded by FusionModules so that the host's
                /// build can tell when two modules register one service differently (MOD0014).
                /// </summary>
                [global::System.AttributeUsage(global::System.AttributeTargets.Assembly, AllowMultiple = true)]
                internal sealed class {{RegistrationAttributeName}} : global::System.Attribute
                {
                    public {{RegistrationAttributeName}}({{Registration.Parameters}})
                    {
                    }
                }
            }

            """;
    }

    /// <summary>
    /// The assembly name as one identifier: <c>Shop.Orders-Api</c> becomes <c>Shop_Orders_Api</c>, and
    /// a keyword is escaped, <c>@event</c>.
    /// </summary>
    private static string Identifier(string assemblyName)
    {
        var text = new StringBuilder(assemblyName.Length + 1);
        if (assemblyName.Length == 0 || !char.IsLetter(assemblyName[0]))
        {
            text.Append('_');
        }

        foreach (var c in assemblyName)
        {
            text.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        var identifier = text.ToString();
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
    }
}
