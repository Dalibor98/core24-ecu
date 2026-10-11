using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Account;

/// <summary>"My orders". Requires login (AuthorizePage in Program.cs).</summary>
public class IndexModel(AppDbContext db, UserManager<AppUser> users) : PageModel
{
    public List<Order> Orders { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var userId = users.GetUserId(User);
        Orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct);
    }
}
