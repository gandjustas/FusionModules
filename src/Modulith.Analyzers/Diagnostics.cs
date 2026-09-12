using Microsoft.CodeAnalysis;

namespace Modulith.Analyzers;

/// <summary>
/// The rule catalogue. Diagnostic ids are permanent once published; the prefix comes from
/// <c>ModulithDiagnosticPrefix</c> so that only a pre-release rename can change it.
/// </summary>
internal static class Diagnostics
{
    private const string DesignCategory = ModulithConstants.PackageName + ".Design";
    private const string HelpLinkFormat = "https://github.com/gandjustas/modulith/blob/main/docs/rules/{0}.md";

    public const string ModuleMustNotExposePublicTypesId = ModulithConstants.DiagnosticPrefix + "0001";
    public const string HostingStartupTypeMustBeModuleId = ModulithConstants.DiagnosticPrefix + "0002";
    public const string HostMustNotUseModuleTypesId = ModulithConstants.DiagnosticPrefix + "0003";
    public const string ApplicationPartMustNotNameModuleId = ModulithConstants.DiagnosticPrefix + "0004";
    public const string ModuleMustBeRegisteredId = ModulithConstants.DiagnosticPrefix + "0005";
    public const string PackageNotReferencedId = ModulithConstants.DiagnosticPrefix + "0020";

    public static readonly DiagnosticDescriptor ModuleMustNotExposePublicTypes =
        Create(ModuleMustNotExposePublicTypesId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor HostingStartupTypeMustBeModule =
        Create(HostingStartupTypeMustBeModuleId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor HostMustNotUseModuleTypes =
        Create(HostMustNotUseModuleTypesId, DiagnosticSeverity.Error, WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ApplicationPartMustNotNameModule =
        Create(ApplicationPartMustNotNameModuleId, DiagnosticSeverity.Error, WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ModuleMustBeRegistered =
        Create(ModuleMustBeRegisteredId, DiagnosticSeverity.Error, WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor PackageNotReferenced =
        Create(PackageNotReferencedId, DiagnosticSeverity.Warning, WellKnownDiagnosticTags.CompilationEnd);

    private static DiagnosticDescriptor Create(string id, DiagnosticSeverity severity, params string[] customTags) =>
        new(
            id,
            new LocalizableResourceString($"{id}_Title", Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString($"{id}_Message", Resources.ResourceManager, typeof(Resources)),
            DesignCategory,
            severity,
            isEnabledByDefault: true,
            description: new LocalizableResourceString($"{id}_Description", Resources.ResourceManager, typeof(Resources)),
            helpLinkUri: string.Format(System.Globalization.CultureInfo.InvariantCulture, HelpLinkFormat, id),
            customTags: customTags);
}
