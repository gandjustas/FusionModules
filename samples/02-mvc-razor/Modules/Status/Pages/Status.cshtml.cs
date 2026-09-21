using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StatusModule.Pages;

// Internal, and nothing had to be done to allow it. Razor Pages does not scan for page models:
// it reads the [RazorCompiledItem] attributes the Razor compiler emits, and the page class it
// generates is itself internal sealed — so an internal model is what that class is built for. What
// must stay public is the constructor (ActivatorUtilities only sees public ones) and the handler
// methods (found with GetMethods()), which is the same bargain a controller strikes.
//
// MOD0001 exempts PageModel for the case where someone does need it public. Needing it is rarer
// than the exemption suggests.
internal class StatusModel : PageModel
{
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    public void OnGet()
    {
    }
}
