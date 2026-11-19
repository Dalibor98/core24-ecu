using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Account;

/// <summary>"My orders", with links to the account settings. Requires login (AuthorizePage in Program.cs).</summary>
public class IndexModel(AppDbContext db, UserManager<AppUser> users) : PageModel
{
    public List<Order> Orders { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        // The workshop has no orders of its own: its home page is the list of customers' orders.
        if (User.IsInRole(Roles.Admin)) return RedirectToPage("/Admin/Index");

        var userId = users.GetUserId(User);
        Orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct);
        return Page();
    }
}
