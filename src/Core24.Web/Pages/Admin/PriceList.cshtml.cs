using Core24.Web.Data;
using Core24.Web.Features.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Admin;

/// <summary>
/// The price list: one line per vehicle + computer. The whole list is loaded from a CSV file;
/// single prices can be corrected here. Changing a price affects new orders only (orders copy their prices).
/// </summary>
public class PriceListModel(AppDbContext db, PriceListImport import, IOptions<AppOptions> app) : PageModel
{
    private const int PageSize = 100;

    public List<PriceListEntry> Entries { get; private set; } = [];
    public int Total { get; private set; }
    public int Matches { get; private set; }
    public string Currency => app.Value.Currency;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>Which page of the list is shown, starting at 1. The name "P" keeps the link short: ?Q=scania&amp;P=2.</summary>
    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(Matches / (double)PageSize));

    [TempData]
    public string? Flash { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Total = await db.PriceList.CountAsync(p => p.IsActive, ct);

        var query = db.PriceList.AsNoTracking();
        // Every word must appear somewhere in the line: "scania s7" finds Scania lines with the EMS S7 computer.
        foreach (var word in (Q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))
        {
            var like = $"%{word}%";
            query = query.Where(p => EF.Functions.Like(p.Brand, like) || EF.Functions.Like(p.Model, like)
                                  || EF.Functions.Like(p.Engine, like) || EF.Functions.Like(p.Ecu, like));
        }
        Matches = await query.CountAsync(ct);
        P = Math.Clamp(P, 1, PageCount); // ?P=999 or ?P=-1 typed by hand still shows a real page
        Entries = await query.OrderBy(p => p.Brand).ThenBy(p => p.Id).Skip((P - 1) * PageSize).Take(PageSize).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string? price, bool isActive, CancellationToken ct)
    {
        var entry = await db.PriceList.FindAsync([id], ct);
        if (entry is null)
            Flash = "That line no longer exists.";
        else if (!Money.TryParseCents(price, out var cents) || cents <= 0)
            Flash = "The price must look like 320 or 320.50.";
        else
        {
            entry.PriceCents = cents;
            entry.IsActive = isActive; // an unticked checkbox sends nothing, so the parameter is false
            await db.SaveChangesAsync(ct);
            Flash = $"Saved {entry.Description}.";
        }
        return RedirectToPage(new { Q, P }); // back to the same page of the list
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            Flash = "Choose a CSV file first.";
        else if (file.Length > 2 * 1024 * 1024)
            Flash = "That file is too large for a price list (2 MB at most).";
        else
        {
            await using var stream = file.OpenReadStream();
            var result = await import.ImportAsync(stream, ct);
            Flash = result.Succeeded
                ? $"Imported: {result.Added} new, {result.Updated} changed, {result.Deactivated} no longer offered."
                : "Nothing was imported. " + string.Join(" ", result.Errors);
        }
        return RedirectToPage();
    }
}
