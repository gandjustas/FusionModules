namespace FusionModules.Analyzers;

/// <summary>
/// Names of the generators' pipeline steps, so that the tests can check each one is cached when
/// nothing it depends on changed.
/// </summary>
internal static class TrackingNames
{
    public const string ProjectKind = nameof(ProjectKind);
    public const string Module = nameof(Module);
    public const string RegistrationCandidates = nameof(RegistrationCandidates);
    public const string Registrations = nameof(Registrations);

    /// <summary><see cref="KnownModulesGenerator"/>: the project kind and whether the list is switched off.</summary>
    public const string KnownModulesSettings = nameof(KnownModulesSettings);

    /// <summary><see cref="KnownModulesGenerator"/>: the module list, the value the generated file is written from.</summary>
    public const string KnownModules = nameof(KnownModules);
}
