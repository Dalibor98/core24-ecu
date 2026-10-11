using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Core24.Web.Data;
using Core24.Web.Features.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Admin;

/// <summary>The fixed price list. Changing a price affects new orders only (orders copy their prices).</summary>
public class ModsModel(AppDbContext db, IOptions<AppOptions> app) : PageModel
{
    public class ModInput
    {
        public int? Id { get; set; }

        [Required, StringLength(120)]
        public string Name { get; set; } = "";

        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// Typed as "290.50" and parsed by us with a dot as the decimal separator. Binding to decimal
        /// directly would use the SERVER's language settings, where "290.50" can mean 29050.
        /// </summary>
        [Required, RegularExpression(@"^\d{1,6}([.,]\d{1,2})?$", ErrorMessage = "Price must look like 290 or 290.50.")]
        public string Price { get; set; } = "";

        // An unticked checkbox sends NOTHING, so the default must be false (ticked sends "true").
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
    }

    public List<Mod> Mods { get; private set; } = [];
    public string Currency => app.Value.Currency;

    [BindProperty]
    public ModInput Input { get; set; } = new();

    [TempData]
    public string? Flash { get; set; }

    public async Task OnGetAsync(CancellationToken ct) =>
        Mods = await db.Mods.OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ToListAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Flash = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToPage();
        }

        var mod = Input.Id is { } id ? await db.Mods.FindAsync([id], ct) : null;
        if (mod is null)
        {
            mod = new Mod { Name = Input.Name };
            db.Mods.Add(mod);
        }
        mod.Name = Input.Name.Trim();
        mod.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        mod.PriceCents = Money.ToCents(decimal.Parse(Input.Price.Replace(',', '.'), CultureInfo.InvariantCulture));
        mod.IsActive = Input.IsActive;
        mod.SortOrder = Input.SortOrder;
        await db.SaveChangesAsync(ct);

        Flash = $"Saved \"{mod.Name}\".";
        return RedirectToPage();
    }
}
