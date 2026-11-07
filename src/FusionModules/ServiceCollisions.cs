using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules;

/// <summary>
/// Notices two modules registering the same service with different implementations.
/// </summary>
/// <remarks>
/// <para>
/// Before the merge, each service had a container of its own, and an interface could mean one thing
/// in each. After the merge there is one container. For a service resolved as a single instance,
/// the last registration wins without anyone being told, so one module quietly gets the other
/// module's implementation. In eShop this was <c>IIntegrationEventLogService</c>: the Catalog and
/// Ordering outboxes each registered their own, and whichever module loaded first wrote its events
/// through the other's DbContext. The symptom was a "transaction is not associated with the current
/// connection" error a long way from the cause.
/// </para>
/// <para>
/// <c>TryAdd</c> does not fix it. It turns "last wins" into "first wins", and one module still gets
/// the wrong implementation. When the two modules are alternatives — two transports, of which each
/// deployment picks one — what fixes it is the module list: no topology should load both. Otherwise
/// it is a keyed registration, or a service type that belongs to each module. When the type really is consumed as <c>IEnumerable&lt;T&gt;</c>, the overlap is
/// intended, and the contract says so itself: an attribute named <c>MultipleImplementationsAttribute</c>
/// on the type, declared wherever the contract is (see <see cref="IsMarkedMultiple"/>). The allow-list
/// in configuration is for types the application does not own.
/// </para>
/// <para>
/// This is checked at runtime because the registrations that collide are often made inside library
/// extension methods, which no analyzer sees, and because only the running host knows which
/// modules a topology puts together.
/// </para>
/// </remarks>
internal static class ServiceCollisions
{
    /// <summary>
    /// Fails startup on a collision.
    /// </summary>
    /// <remarks>
    /// There is no switch to make it a warning. Every collision a host has to live with has a name,
    /// and naming it in <see cref="TopologyConventions.AllowedServiceCollisionsKey"/> answers for that
    /// type and no other; a switch would answer for every type, including the ones a later change
    /// would add.
    /// </remarks>
    /// <returns>
    /// The service types several modules register with different implementations on purpose —
    /// marked on the contract or allowed in configuration — for the startup report.
    /// </returns>
    public static IReadOnlyList<string> Check(IReadOnlyList<ModuleRegistration> added, IReadOnlySet<string> allowed, IConfiguration configuration)
    {
        var (collisions, shared) = Find(added, allowed);

        return collisions.Count == 0
            ? shared
            : throw new InvalidOperationException(Describe(collisions, TypeNames.ReadList(configuration, TopologyConventions.AlternativeServicesKey)));
    }

    private static (List<Collision> Collisions, List<string> Shared) Find(IReadOnlyList<ModuleRegistration> added, IReadOnlySet<string> allowed)
    {
        var collisions = new List<Collision>();
        var shared = new List<string>();

        var candidates = added
            .Where(entry => !IsFrameworkContract(entry.Descriptor.ServiceType))
            .GroupBy(entry => (entry.Descriptor.ServiceType, Key: GetKey(entry.Descriptor)));
        foreach (var group in candidates)
        {
            var serviceType = group.Key.ServiceType;

            // The same implementation registered by two modules is redundant rather than wrong:
            // whichever wins, every module gets what it asked for. "The same" is the same type, not
            // the same name: two modules' own Calculator classes are two implementations, and
            // comparing names let dotnext-2026's Local and RemoteGrpc transports start together.
            var byModule = group
                .GroupBy(entry => entry.Module)
                .Select(perModule => (Module: perModule.Key, Implementations: perModule.Select(e => Implementation(e.Descriptor)).ToList()))
                .ToList();

            var first = byModule[0].Implementations.Select(i => i.Key).ToHashSet();
            if (byModule.Count < 2 || byModule.All(m => first.SetEquals(m.Implementations.Select(i => i.Key))))
            {
                continue;
            }

            // Judged only once there is an overlap, so that the report names the contracts that
            // are actually shared rather than every type the allow-list happens to mention.
            if (IsMarkedMultiple(serviceType) || TypeNames.Matches(serviceType, allowed))
            {
                shared.Add(TypeNames.Readable(serviceType, full: false));
                continue;
            }

            // Names for the message: short, unless two different implementations would read the same.
            var all = byModule.SelectMany(m => m.Implementations).DistinctBy(i => i.Key).ToList();
            var ambiguous = all.GroupBy(i => i.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            collisions.Add(new Collision(serviceType, group.Key.Key, byModule
                .Select(m => (m.Module, string.Join(", ", m.Implementations.DistinctBy(i => i.Key).Select(i => ambiguous.Contains(i.Name) ? i.FullName : i.Name))))
                .ToList()));
        }

        return (collisions, shared.Distinct().ToList());
    }

    private static object? GetKey(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService ? descriptor.ServiceKey : null;

    /// <summary>
    /// What a descriptor resolves to: a type (its own, or its instance's) or a factory method. The key
    /// is compared — the type or the method itself — and the names are for the message.
    /// </summary>
    private static Registered Implementation(ServiceDescriptor descriptor)
    {
        var type = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
        if (type is not null)
        {
            return new(type, TypeNames.Readable(type, full: false), TypeNames.Readable(type, full: true));
        }

        Delegate? factory = descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory;
        return new((object?)factory?.Method ?? "?", FactoryName(factory, full: false), FactoryName(factory, full: true));
    }

    private static string FactoryName(Delegate? factory, bool full) =>
        "factory " + (factory is null ? "?" : $"{TypeNames.Readable(factory.Method.DeclaringType, full)}.{factory.Method.Name}");

    /// <summary>
    /// The framework's own contracts, and the library ones several registrations share by design
    /// (<see cref="TopologyConventions.SharedByDesign"/>): EF Core documents its non-generic
    /// <c>DbContextOptions</c> as ambiguous with two contexts in one container — found migrating
    /// eShop, whose Catalog and Webhooks modules each keep a context.
    /// </summary>
    private static bool IsFrameworkContract(Type serviceType) =>
        IsFrameworkType(serviceType) || (serviceType.FullName is { } name && TopologyConventions.SharedByDesign.Contains(name));

    /// <summary>
    /// Whether the type comes from the framework's own assemblies, whose contracts are registered to
    /// be shared (<see cref="TopologyConventions.IsFrameworkAssembly"/>).
    /// </summary>
    internal static bool IsFrameworkType(Type type) => TopologyConventions.IsFrameworkAssembly(type.Assembly.GetName().Name);

    /// <summary>
    /// Whether the contract declares that several modules implement it, by carrying an attribute
    /// named <c>MultipleImplementationsAttribute</c>, in any namespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched by name, the way the compiler recognises its own attributes, so that a contracts
    /// library declares a two-line internal attribute instead of referencing this package. A
    /// contracts library is referenced by every module and by the host, and should not drag a
    /// modularity package along with it.
    /// </para>
    /// <para>
    /// It belongs on the contract because that is where the knowledge is. The allow-list in
    /// configuration had to be repeated in every host, test hosts included, and a host that forgot
    /// it refused to start for a reason nobody looking at the contract could see. Found in the
    /// in-process events sample, whose listener contract is implemented by two modules.
    /// </para>
    /// </remarks>
    private static bool IsMarkedMultiple(Type serviceType)
    {
        var declared = serviceType.IsGenericType ? serviceType.GetGenericTypeDefinition() : serviceType;
        return declared.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == TopologyConventions.MultipleImplementationsAttributeName);
    }

    private static string Describe(List<Collision> collisions, IReadOnlySet<string> alternatives)
    {
        var text = new StringBuilder();
        text.Append(collisions.Count == 1 ? "Two modules register the same service" : $"Modules register the same services ({collisions.Count})")
            .AppendLine(" with different implementations. Only the last registration is resolved, so a module may be given another module's implementation:");

        foreach (var collision in collisions)
        {
            text.Append("  ").Append(TypeNames.Readable(collision.ServiceType, full: true));
            if (collision.Key is not null)
            {
                text.Append(" [key: ").Append(collision.Key).Append(']');
            }

            text.AppendLine();
            foreach (var (module, implementations) in collision.Registrations)
            {
                text.Append("    ").Append(module).Append(": ").AppendLine(implementations);
            }
        }

        // A contract the host's build lists as implemented by alternatives is known to be one: the
        // build said so, and the message need not ask. Listing it there is not an allowance here —
        // two alternatives loaded together still hand one module the other's implementation. The
        // modules are named by name, not in the order they registered: the lines above show which
        // registered last, and this sentence should read the same whatever HOSTINGSTARTUPASSEMBLIES'
        // order.
        var listed = collisions.Where(collision => TypeNames.Matches(collision.ServiceType, alternatives)).ToList();
        foreach (var collision in listed)
        {
            text.Append(TypeNames.Readable(collision.ServiceType, full: true))
                .Append(" is listed in the host's build as implemented by alternative modules (fusion_modules_allowed_service_collisions): load only one of ")
                .Append(string.Join(", ", collision.Registrations.Select(registration => registration.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                .AppendLine(" in a topology — remove the others from HOSTINGSTARTUPASSEMBLIES.");
        }

        if (listed.Count == collisions.Count)
        {
            return text.ToString().TrimEnd();
        }

        // Alternatives first: two transports, two implementations of one capability, of which a
        // deployment picks one. dotnext-2026's Local and RemoteGrpc transports are such a pair, and
        // the advice that came first — key the registration — was the wrong fix for them.
        text.AppendLine("If these modules are alternatives that no topology should load together, remove one of them from HOSTINGSTARTUPASSEMBLIES.")
            .AppendLine("Otherwise register a keyed service, or a service type of each module's own. TryAdd does not help: first-wins hands a module the wrong implementation just as last-wins does.")
            .Append("If the type is meant to be consumed as IEnumerable<T>, put an attribute named ").Append(TopologyConventions.MultipleImplementationsAttributeName)
            .Append(" on it (declared anywhere; matched by name), or list it under ").Append(TopologyConventions.AllowedServiceCollisionsKey)
            .Append(" when the type is not yours.");

        return text.ToString();
    }

    private sealed record Collision(Type ServiceType, object? Key, List<(string Module, string Implementations)> Registrations);

    private readonly record struct Registered(object Key, string Name, string FullName);
}
