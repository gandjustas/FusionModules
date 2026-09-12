using Microsoft.AspNetCore.Mvc;

namespace DashboardModule.Areas.Dashboard.Controllers;

// Public because MVC finds controllers by reflection. MOD0001 exempts ControllerBase for
// exactly this reason — see docs/rules/MOD0001.md.
[Area(Module.AreaName)]
public class HomeController : Controller
{
    public IActionResult Index() => View();
}
