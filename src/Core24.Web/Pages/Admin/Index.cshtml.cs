using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Admin;

/// <summary>The workshop's order list. Everything under /Admin requires the Admin role (Program.cs).</summary>
public class IndexModel(AppDbContext db) : PageModel
{
    public List<Order> Orders { get; private set; } = [];
    public OrderStatus? Filter { get; private set; }
    public Dictionary<OrderStatus, int> Counts { get; private set; } = [];

    public async Task OnGetAsync(OrderStatus? status, CancellationToken ct)
    {
        Filter = status;
        Counts = await db.Orders.GroupBy(o => o.Status).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        var query = db.Orders.AsNoTracking();
        if (status is not null) query = query.Where(o => o.Status == status);
        Orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(200).ToListAsync(ct);
    }
}
