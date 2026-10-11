using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Admin;

/// <summary>
/// One order, for the workshop. Each button posts to its own handler (OnPostStart, OnPostUpload...),
/// and every handler ends with a redirect back here (Post/Redirect/Get).
/// </summary>
[RequestSizeLimit(UploadOptions.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = UploadOptions.MaxRequestBytes)]
public class OrderModel(AppDbContext db, OrderService orders) : PageModel
{
    public Order Order { get; private set; } = null!;

    /// <summary>One-time message after a redirect. TempData survives exactly one redirect.</summary>
    [TempData]
    public string? Flash { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items).Include(o => o.Files).Include(o => o.Payments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();
        Order = order;
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken ct)
    {
        Flash = await orders.StartWorkAsync(id, ct) ? "Marked as in progress." : "The order was not in 'Submitted' any more.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadAsync(Guid id, List<IFormFile> modifiedFiles, CancellationToken ct)
    {
        var errors = await orders.AddModifiedFilesAsync(id, modifiedFiles, ct);
        Flash = errors.Count == 0 ? $"Uploaded {modifiedFiles.Count} file(s)." : string.Join(" ", errors.Select(e => e.Message));
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteFileAsync(Guid id, int fileId, CancellationToken ct)
    {
        Flash = await orders.RemoveModifiedFileAsync(id, fileId, ct) ? "File removed." : "That file can't be removed now.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReadyAsync(Guid id, CancellationToken ct)
    {
        var error = await orders.MarkReadyAsync(id, ct);
        Flash = error?.Message ?? "Marked ready. The customer got the 'please pay' email.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        Flash = await orders.CancelAsync(id, ct) ? "Order cancelled." : "This order can't be cancelled any more.";
        return RedirectToPage(new { id });
    }
}
