using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StatusModule.Pages;

// Public because Razor Pages finds page models by reflection; MOD0001 exempts PageModel.
public class StatusModel : PageModel
{
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    public void OnGet()
    {
    }
}
