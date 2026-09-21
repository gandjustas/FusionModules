using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;

namespace Modulith;

/// <summary>
/// Lets MVC find types that are not public.
/// </summary>
/// <remarks>
/// <para>
/// MVC requires a controller or a view component to be a public type, and in a module that
/// requirement does not stop at the type: a public class cannot take an internal service in a
/// public constructor or return an internal model from a public method, so the services and the
/// models go public with it, and then whatever those name. Most of the module ends up public,
/// which is the opposite of what MOD0001 is for.
/// </para>
/// <para>
/// Not everything the framework reaches by reflection needs this. Razor Pages never did — page
/// discovery reads the attributes the Razor compiler emits rather than scanning types, and the
/// page class it generates is itself internal, so an internal <c>PageModel</c> works as it is.
/// Tag helpers cannot be helped at all: <c>@addTagHelper</c> is resolved by the Razor compiler
/// against Roslyn symbols, and it requires public, so an internal tag helper simply produces no
/// descriptor and its element is emitted as literal HTML with no diagnostic anywhere. Keep tag
/// helpers public.
/// </para>
/// </remarks>
public static class ModuleDiscovery
{
    /// <summary>
    /// Replaces MVC's controller discovery with one that does not require a public type.
    /// </summary>
    /// <param name="builder">The MVC builder to configure.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// <para>
    /// The stock <see cref="ControllerFeatureProvider"/> requires <c>TypeInfo.IsPublic</c>. In a
    /// module that requirement does not stop at the controller: a public constructor cannot take
    /// an internal service and a public action cannot return an internal DTO, so the database
    /// context, the services and every DTO have to go public with it — which is most of the
    /// module, and the opposite of what MOD0001 is for. Exempting controllers from that rule, as
    /// the analyzer does, is true and not enough.
    /// </para>
    /// <para>
    /// Turn it round instead: keep the controller internal and let discovery see it. The class
    /// becomes <c>internal</c>; <b>the constructor stays public</b>, because MVC's activator reads
    /// <c>GetConstructors()</c>, which is public-only. That is legal and not a contradiction — C#
    /// bounds a member's effective accessibility by its containing type, so a public constructor
    /// on an internal class may take internal parameters, and a public action on one may return
    /// internal types.
    /// </para>
    /// <para>
    /// One call is enough for the whole application: application parts are a single list, and this
    /// configures how all of them are read. Calling it from several modules is harmless.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =&gt;
    ///     services.AddControllers()
    ///         .AddApplicationPart(typeof(Module).Assembly)
    ///         .AllowInternalControllers();
    /// </code>
    /// </example>
    public static IMvcBuilder AllowInternalControllers(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigureApplicationPartManager(manager =>
        {
            var providers = manager.FeatureProviders;

            // In place, not appended: providers run in list order, and a provider that filters
            // controllers would otherwise run before discovery and see nothing to filter.
            var index = -1;
            for (var i = providers.Count - 1; i >= 0; i--)
            {
                if (providers[i] is ControllerFeatureProvider)
                {
                    index = i;
                    providers.RemoveAt(i);
                }
            }

            if (index < 0)
            {
                // MVC has not registered its own yet — there is nothing to replace, so append.
                // Not the double-call case: this provider is itself a ControllerFeatureProvider,
                // so a second call finds and removes it above and never reaches here. The guard
                // stays because an appended provider must not be appended twice either.
                if (providers.Any(provider => provider is ModuleControllerFeatureProvider))
                {
                    return;
                }

                providers.Add(new ModuleControllerFeatureProvider());
                return;
            }

            providers.Insert(index, new ModuleControllerFeatureProvider());
        });
    }

    /// <summary>
    /// Lets MVC find view components that are not public.
    /// </summary>
    /// <param name="builder">The MVC builder to configure.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// <para>
    /// The same cascade as for controllers, and the same answer — but reached differently. MVC's
    /// test for a view component lives in a static method, not a virtual one, so there is nothing
    /// to override. There is also nothing to replace: the application part manager runs every
    /// provider for a feature and the list of view components is open, so this adds a second
    /// provider that contributes the internal ones and leaves the stock provider alone.
    /// </para>
    /// <para>
    /// Invoke it from a view either by name — <c>Component.InvokeAsync("Tiles")</c> — or through
    /// the generic overload, <c>Component.InvokeAsync&lt;TilesViewComponent&gt;()</c>. Both are
    /// resolved at run time against the descriptor collection this provider contributed to, and the
    /// generic form compiles because the class Razor generates for a view is itself internal and
    /// lives in the same assembly as the component.
    /// </para>
    /// <para>
    /// <b>What does not work is the <c>&lt;vc:tiles&gt;</c> element.</b> That form is bound by the
    /// Razor compiler against Roslyn symbols, and it requires a public type for the same reason a
    /// tag helper does. When it finds none it reports nothing: the element is copied into the page
    /// as literal HTML, with no error, no warning and no exception. If you want the element syntax,
    /// the component stays public.
    /// </para>
    /// <para>
    /// As with a controller, the <c>Invoke</c> or <c>InvokeAsync</c> method stays public: MVC finds
    /// it with public-only binding flags, and a public method on an internal class may still take
    /// and return internal types.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =&gt;
    ///     services.AddControllersWithViews()
    ///         .AddApplicationPart(typeof(Module).Assembly)
    ///         .AllowInternalViewComponents();
    /// </code>
    /// </example>
    public static IMvcBuilder AllowInternalViewComponents(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigureApplicationPartManager(manager =>
        {
            // Appended rather than substituted, so calling it from several modules is harmless and
            // so a third-party provider for this feature keeps working.
            if (manager.FeatureProviders.Any(provider => provider is ModuleViewComponentFeatureProvider))
            {
                return;
            }

            manager.FeatureProviders.Add(new ModuleViewComponentFeatureProvider());
        });
    }

    /// <summary>
    /// Contributes the view components MVC's own provider passed over for being internal.
    /// </summary>
    private sealed class ModuleViewComponentFeatureProvider : IApplicationFeatureProvider<ViewComponentFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ViewComponentFeature feature)
        {
            var types = parts
                .OfType<IApplicationPartTypeProvider>()
                .SelectMany(part => part.Types)
                .Where(IsInternalViewComponent);

            foreach (var type in types)
            {
                // The stock provider runs too, and the order of providers is not this one's to
                // assume.
                if (!feature.ViewComponents.Contains(type))
                {
                    feature.ViewComponents.Add(type);
                }
            }
        }

        /// <summary>
        /// MVC's own view component test, for the types it rejects only for not being public.
        /// </summary>
        private static bool IsInternalViewComponent(TypeInfo typeInfo)
        {
            if (typeInfo.IsPublic) return false;
            if (!typeInfo.IsClass) return false;
            if (typeInfo.IsAbstract) return false;
            // Nested is the other thing IsPublic excludes, and excluding it keeps this the same
            // shape as controller discovery rather than quietly wider.
            if (typeInfo.IsNested) return false;
            if (typeInfo.ContainsGenericParameters) return false;
            if (typeInfo.IsDefined(typeof(NonViewComponentAttribute))) return false;

            return typeInfo.Name.EndsWith("ViewComponent", StringComparison.OrdinalIgnoreCase) ||
                   typeInfo.IsDefined(typeof(ViewComponentAttribute));
        }
    }

    /// <summary>
    /// MVC's own controller test, minus the requirement that the type be public.
    /// </summary>
    /// <remarks>
    /// A third-party <see cref="ControllerFeatureProvider"/> subclass is discarded along with the
    /// stock one. That is unavoidable: two providers append into one list, so leaving it would
    /// discover every public controller twice.
    /// </remarks>
    private sealed class ModuleControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo)
        {
            if (!typeInfo.IsClass) return false;
            if (typeInfo.IsAbstract) return false;
            // The base rejected these through IsPublic, which is false for any nested type.
            if (typeInfo.IsNested) return false;
            if (typeInfo.ContainsGenericParameters) return false;
            if (typeInfo.IsDefined(typeof(NonControllerAttribute))) return false;

            return typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
                   typeInfo.IsDefined(typeof(ControllerAttribute));
        }
    }
}
