using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FusionModules;

/// <summary>
/// Notices two modules configuring the same options differently — settings that, after the merge,
/// reach every module's endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Each service used to configure <c>JsonOptions</c>, <c>HubOptions</c>, <c>GrpcServiceOptions</c>
/// or <c>RateLimiterOptions</c> for itself. In one process there is one of each: every module's
/// <c>Configure</c> runs against the same instance, so a module's JSON converter, hub limit or gRPC
/// interceptor applies to its neighbours too, and a value two modules both set is whichever ran last.
/// The collision check does not see this — options are the framework's own contracts, registered to
/// be read as a sequence — so this looks at what the configuring delegates are instead.
/// </para>
/// <para>
/// It warns rather than fails: several modules adding to the same options is often what they mean
/// (each registers its own JSON converter). It looks only at the default, unnamed options, and only at
/// delegates declared in the module's own assembly — the module's own settings. A delegate declared in a
/// library is that library's: the framework's, a package's (Swashbuckle adds an MVC convention from
/// <c>AddSwaggerGen</c>), or a helper the application wrote for its modules. Each module that calls it
/// runs the same code. Counted, one module's <c>AddSwaggerGen</c> and a shared helper that adds a
/// convention scoped to the calling module's controllers would read as every module configuring MVC
/// differently, on every start.
/// </para>
/// </remarks>
internal static class SharedOptions
{
    /// <summary>Logs one warning listing the options several modules configure differently.</summary>
    public static void Warn(IReadOnlyList<ModuleRegistration> added, IReadOnlySet<string> allowed, IServiceProvider services)
    {
        var shared = Find(added).Where(entry => !TypeNames.Matches(entry.Options, allowed)).ToList();
        if (shared.Count == 0)
        {
            return;
        }

        var list = string.Join("; ", shared.Select(entry => $"{TypeNames.Readable(entry.Options, full: false)} ({string.Join(", ", entry.Modules)})"));
        services.GetService<ILoggerFactory>()?.CreateLogger(nameof(FusionModules)).LogWarning(
            "Options configured differently by more than one module apply to all of them: {SharedOptions}. " +
            "Each module's settings reach the others' endpoints, and a value they both set is whichever ran last. " +
            "Configure per hub, per endpoint or per named instance where the framework allows it (AddHubOptions<THub>, a named scheme), " +
            "or list the options type under " + TopologyConventions.AllowedServiceCollisionsKey + " if sharing is intended.",
            list);
    }

    /// <summary>The options types more than one module configures, each with a delegate of its own.</summary>
    internal static List<(Type Options, List<string> Modules)> Find(IReadOnlyList<ModuleRegistration> added) =>
        added
            .Select(entry => (entry.Module, Options: OptionsType(entry.Descriptor), entry.Assembly, entry.Descriptor))
            .Where(entry => entry.Options is not null)
            .Select(entry => (entry.Module, entry.Options, entry.Assembly, Action: Action(entry.Descriptor)))

            // Declared in the module's own assembly; a lambda's closure class is nested in the type that wrote it.
            .Where(entry => entry.Action is not null && entry.Action.Method.DeclaringType?.Assembly == entry.Assembly)
            .GroupBy(entry => entry.Options!)
            .Where(group => group.Select(entry => entry.Action!.Method).Distinct().Count() > 1 &&
                            group.Select(entry => entry.Module).Distinct().Count() > 1)
            .Select(group => (group.Key, group.Select(entry => entry.Module).Distinct().ToList()))
            .ToList();

    /// <summary>The <c>T</c> of an <c>IConfigureOptions&lt;T&gt;</c> or <c>IPostConfigureOptions&lt;T&gt;</c> registration.</summary>
    private static Type? OptionsType(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService || !descriptor.ServiceType.IsGenericType)
        {
            return null;
        }

        var definition = descriptor.ServiceType.GetGenericTypeDefinition();
        return definition == typeof(IConfigureOptions<>) || definition == typeof(IPostConfigureOptions<>)
            ? descriptor.ServiceType.GetGenericArguments()[0]
            : null;
    }

    /// <summary>
    /// The configuring delegate of a <c>Configure</c>/<c>PostConfigure</c> registration, when it
    /// configures the default or all instances and was written outside the framework.
    /// </summary>
    /// <remarks>
    /// Read only from the framework's own <c>ConfigureNamedOptions</c> and
    /// <c>PostConfigureNamedOptions</c>: an instance of any other type is the application's, and its
    /// properties are not this check's to call.
    /// </remarks>
    private static Delegate? Action(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not { } instance || !IsNamedOptionsDelegate(instance.GetType()))
        {
            return null;
        }

        var type = instance.GetType();
        var name = type.GetProperty("Name")?.GetValue(instance) as string;
        if (name is not null && name != Options.DefaultName)
        {
            return null;
        }

        if (type.GetProperty("Action")?.GetValue(instance) is not Delegate action)
        {
            return null;
        }

        return action.Method.DeclaringType is { } declaring && ServiceCollisions.IsFrameworkType(declaring) ? null : action;
    }

    /// <summary><c>ConfigureNamedOptions&lt;…&gt;</c> or <c>PostConfigureNamedOptions&lt;…&gt;</c>, of any arity.</summary>
    private static bool IsNamedOptionsDelegate(Type type) =>
        type.IsGenericType &&
        type.Assembly == typeof(ConfigureNamedOptions<>).Assembly &&
        (type.Name.StartsWith("ConfigureNamedOptions`", StringComparison.Ordinal) || type.Name.StartsWith("PostConfigureNamedOptions`", StringComparison.Ordinal));
}
