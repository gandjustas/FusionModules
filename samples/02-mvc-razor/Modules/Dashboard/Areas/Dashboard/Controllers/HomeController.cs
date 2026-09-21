using Microsoft.AspNetCore.Mvc;

namespace DashboardModule.Areas.Dashboard.Controllers;

// Internal, like the rest of the module, which is what MOD0001 asks for and what the stock
// ControllerFeatureProvider forbids — it requires a public type. Module.cs replaces that provider
// through AllowInternalControllers, and the cascade this avoids is the point: were the class
// public, its public constructor could not take the internal Tiles and its public action could not
// return the internal DashboardView, so both of those would have to go public too, and then
// whatever they name, until most of the module is.
//
// The constructor is still public, and has to be: MVC's activator reads GetConstructors(), which
// is public-only. A public constructor on an internal class is not a contradiction — C# bounds a
// member's effective accessibility by its containing type, which is exactly why it may take
// internal parameters.
[Area(Module.AreaName)]
internal class HomeController(Tiles tiles) : Controller
{
    public IActionResult Index() => View(new DashboardView(tiles.Count));
}

sealed class Tiles
{
    public int Count => 3;
}

sealed record DashboardView(int TileCount);
