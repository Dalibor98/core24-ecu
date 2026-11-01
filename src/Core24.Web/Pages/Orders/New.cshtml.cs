using Core24.Web.Data;
using Core24.Web.Features.Catalog;
using Core24.Web.Features.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Orders;

// Upload requests can be large: raise the limits for THIS page only, not the whole site.
[RequestSizeLimit(UploadOptions.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = UploadOptions.MaxRequestBytes)]
[EnableRateLimiting("uploads")]
public class NewModel(AppDbContext db, OrderService orders, UserManager<AppUser> users, IOptions<UploadOptions> uploadOptions, IOptions<AppOptions> appOptions) : PageModel
{
    // Same list as the landing page's booking form.
    public static readonly string[] Makes = ["Mercedes-Benz", "Volvo", "Scania", "MAN", "DAF", "Iveco", "Renault Trucks", "Other"];

    [BindProperty]
    public NewOrderInput Input { get; set; } = new();

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    public List<Mod> Mods { get; private set; } = [];
    public UploadOptions Uploads => uploadOptions.Value;
    public string Currency => appOptions.Value.Currency;
    public bool SignedIn => User.Identity?.IsAuthenticated == true;

    public async Task<IActionResult> OnGetAsync()
    {
        // The workshop (admin) handles orders from the admin area, not the customer form.
        if (User.IsInRole(Roles.Admin)) return RedirectToPage("/Admin/Index");

        await LoadModsAsync();
        Input.Make = Makes[0];

        // Logged-in customers don't retype their email.
        if (await users.GetUserAsync(User) is { } user)
            Input.Email = user.Email ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Same rule on submit: an admin can't place a customer order even by posting the form directly.
        if (User.IsInRole(Roles.Admin)) return RedirectToPage("/Admin/Index");

        var user = await users.GetUserAsync(User);
        if (user is not null)
        {
            // The order must belong to the logged-in account's email, whatever the form said.
            Input.Email = user.Email ?? Input.Email;
            ModelState.Remove("Input.Email");
        }

        if (!ModelState.IsValid)
        {
            await LoadModsAsync();
            return Page();
        }

        var result = await orders.CreateAsync(Input, Files, user?.Id, ct);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Field == "ModIds" ? "Input.ModIds" : error.Field, error.Message);
            await LoadModsAsync();
            return Page();
        }

        // Post/Redirect/Get: refreshing the next page doesn't submit the order again.
        return Redirect($"/o/{result.Order!.AccessToken}?new=1");
    }

    private async Task LoadModsAsync() =>
        Mods = await db.Mods.Where(m => m.IsActive).OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ToListAsync();
}
