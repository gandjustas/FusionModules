using Microsoft.CodeAnalysis;

namespace Modulith.Analyzers;

/// <summary>
/// The rule catalogue. Diagnostic ids are permanent once published, and the resource keys in
/// Resources.resx are spelled to match them, so changing one means changing both.
/// </summary>
internal static class Diagnostics
{
    private const string DesignCategory = "Modulith.Design";
    private const string UsageCategory = "Modulith.Usage";
    private const string HelpLinkFormat = "https://github.com/gandjustas/modulith/blob/main/docs/rules/{0}.md";

    public const string ModuleMustNotExposePublicTypesId = "MOD0001";
    public const string HostingStartupTypeMustBeModuleId = "MOD0002";
    public const string HostMustNotUseModuleTypesId = "MOD0003";
    public const string ApplicationPartMustNotNameModuleId = "MOD0004";
    public const string ModuleMustBeRegisteredId = "MOD0005";
    public const string ModuleMustNotReplacePipelineId = "MOD0006";
    public const string RedundantStartupFilterId = "MOD0007";
    public const string HostedServiceInModuleId = "MOD0008";
    public const string HubClientTypeMustBeReachableId = "MOD0009";
    public const string PackageNotReferencedId = "MOD0020";

    public static readonly DiagnosticDescriptor ModuleMustNotExposePublicTypes =
        Create(ModuleMustNotExposePublicTypesId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor HostingStartupTypeMustBeModule =
        Create(HostingStartupTypeMustBeModuleId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor HostMustNotUseModuleTypes =
        Create(HostMustNotUseModuleTypesId, DiagnosticSeverity.Error, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ApplicationPartMustNotNameModule =
        Create(ApplicationPartMustNotNameModuleId, DiagnosticSeverity.Error, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ModuleMustBeRegistered =
        Create(ModuleMustBeRegisteredId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ModuleMustNotReplacePipeline =
        Create(ModuleMustNotReplacePipelineId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RedundantStartupFilter =
        Create(RedundantStartupFilterId, DiagnosticSeverity.Warning, UsageCategory);

    public static readonly DiagnosticDescriptor HostedServiceInModule =
        Create(HostedServiceInModuleId, DiagnosticSeverity.Warning, UsageCategory);

    public static readonly DiagnosticDescriptor HubClientTypeMustBeReachable =
        Create(HubClientTypeMustBeReachableId, DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor PackageNotReferenced =
        Create(PackageNotReferencedId, DiagnosticSeverity.Warning);

    private static DiagnosticDescriptor Create(string id, DiagnosticSeverity severity, string? category = null, params string[] customTags) =>
        new(
            id,
            new LocalizableResourceString($"{id}_Title", Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString($"{id}_Message", Resources.ResourceManager, typeof(Resources)),
            category ?? DesignCategory,
            severity,
            isEnabledByDefault: true,
            description: new LocalizableResourceString($"{id}_Description", Resources.ResourceManager, typeof(Resources)),
            helpLinkUri: string.Format(System.Globalization.CultureInfo.InvariantCulture, HelpLinkFormat, id),
            customTags: customTags);
}
