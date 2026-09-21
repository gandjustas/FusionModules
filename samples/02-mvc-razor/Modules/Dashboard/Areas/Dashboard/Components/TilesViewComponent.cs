using Microsoft.AspNetCore.Mvc;
using DashboardModule.Areas.Dashboard.Controllers;

namespace DashboardModule.Areas.Dashboard.Components;

// Internal, like the controller and for the same reason: a public view component could not take
// the internal Tiles service in a public constructor, so Tiles would go public, and so on.
//
// What differs from the controller is how it is found. MVC's test for a view component is a static
// method with nothing to override, so AllowInternalViewComponents adds a second feature provider
// alongside the stock one instead of replacing it.
//
// The price is one call site, and only one. Index.cshtml reaches this both by name and through
// the generic overload — the generated view class is itself internal and in this assembly, so it
// can name an internal type, and the lookup happens at runtime either way.
//
// The <vc:tiles> element is the exception. It is bound by the Razor compiler, which requires a
// public type, and when it finds none it says nothing: the element is copied into the page as
// literal HTML with no error and no warning. Verified, not assumed.
internal class TilesViewComponent(Tiles tiles) : ViewComponent
{
    // Public, because MVC looks for Invoke with public-only binding flags. A public method on an
    // internal class may still take and return internal types.
    public IViewComponentResult Invoke() => Content($"{tiles.Count} tiles, counted by a view component.");
}
