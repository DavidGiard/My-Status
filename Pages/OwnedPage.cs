using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MyStatus.Pages;

public abstract class OwnedPage : PageModel
{
    protected string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authentication is required.");
    protected void Conflict() => ModelState.AddModelError("", "This record changed or was deleted in another session. Your changes were not saved. Reload the page before trying again.");
    protected void SaveError(DbUpdateException error)
    {
        HttpContext.RequestServices.GetRequiredService<ILogger<OwnedPage>>()
            .LogWarning("Database save rejected for {Page}; exception type {ExceptionType}.", GetType().Name, error.GetType().Name);
        ModelState.AddModelError("", "The change could not be saved. A referenced record may have changed, or this name may already exist. Reload and try again.");
    }
}
