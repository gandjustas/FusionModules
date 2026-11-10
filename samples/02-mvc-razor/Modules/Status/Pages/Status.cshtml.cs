using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StatusModule.Pages;

// Internal, as MOD0001 requires of a page model like any other type, and nothing had to be done to
// allow it. Razor Pages does not scan for page models: it reads the [RazorCompiledItem] attributes
// the Razor compiler emits, and the page class it generates is itself internal sealed — so an
// internal model is what that class is built for. What must stay public is the constructor
// (ActivatorUtilities only sees public ones) and the handler methods (found with GetMethods()),
// which is the same bargain a controller strikes.
internal class StatusModel : PageModel
{
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    public void OnGet()
    {
    }
}
