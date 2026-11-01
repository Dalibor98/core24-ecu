using Core24.Web.Data;
using Core24.Web.Features.Downloads;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Orders;

/// <summary>
/// The customer's order page. Access rule: whoever has the link (the token) may see it.
/// Logged-in customers reach the same page from "My orders".
/// </summary>
public class DetailsModel(AppDbContext db, PaymentService payments, DownloadLinks links) : PageModel
{
    public Order Order { get; private set; } = null!;

    /// <summary>File id → fresh download URL, only when paid. Made on every page view, valid for 10 minutes.</summary>
    public Dictionary<int, string> DownloadUrls { get; } = [];

    public bool JustCreated { get; private set; }
    public bool ReturnedFromPayment { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, bool @new, bool paid, CancellationToken ct)
    {
        if (!await LoadAsync(token, ct)) return NotFound();
        JustCreated = @new;
        ReturnedFromPayment = paid && Order.Status == OrderStatus.Ready; // back from Stripe, webhook not here yet

        if (Order is { Status: OrderStatus.Paid, FilesDeletedAtUtc: null })
            foreach (var file in Order.ModifiedFiles)
                DownloadUrls[file.Id] = links.CreateUrl(file.Id);

        return Page();
    }

    public async Task<IActionResult> OnPostPayAsync(string token, CancellationToken ct)
    {
        var url = await payments.StartCheckoutAsync(token, ct);
        if (url is not null) return Redirect(url);

        if (!await LoadAsync(token, ct)) return NotFound();
        Error = "This order can't be paid right now. Refresh the page to see its current status.";
        return Page();
    }

    private async Task<bool> LoadAsync(string token, CancellationToken ct)
    {
        // Secret in the URL: tell browsers and proxies not to store or forward it.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.Files)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.AccessToken == token, ct);
        if (order is null) return false;
        Order = order;
        return true;
    }
}
