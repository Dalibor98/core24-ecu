using Core24.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Features.Catalog;

public sealed record PriceListImportResult(int Added, int Updated, int Deactivated, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Loads the whole price list from a CSV file (Excel: "Save as CSV"). First line: brand;model;engine;ecu;price.
/// The file is the truth: lines in it are added or updated, lines missing from it are deactivated
/// (never deleted, old orders point at them). If any line is wrong, nothing is changed.
/// </summary>
public class PriceListImport(AppDbContext db)
{
    private static readonly string[] Columns = ["brand", "model", "engine", "ecu", "price"];

    public async Task<PriceListImportResult> ImportAsync(Stream csv, CancellationToken ct = default)
    {
        // The StreamReader detects and skips the invisible "BOM" mark Excel puts at the start of UTF-8 files.
        using var reader = new StreamReader(csv);
        var lines = (await reader.ReadToEndAsync(ct)).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(lines[0]))
            return Fail("The file is empty.");

        // Excel uses ";" in most of Europe and "," elsewhere: take whichever the header line uses.
        var separator = lines[0].Contains(';') ? ';' : ',';
        var header = Split(lines[0], separator).Select(h => h.ToLowerInvariant()).ToList();
        var missing = Columns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
            return Fail($"The first line must name the columns brand, model, engine, ecu, price. Missing: {string.Join(", ", missing)}.");
        var at = Columns.ToDictionary(c => c, c => header.IndexOf(c));

        var errors = new List<string>();
        var rows = new Dictionary<(string, string, string, string), long>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = Split(lines[i], separator);
            string Cell(string column) => at[column] < cells.Count ? cells[at[column]] : "";

            var key = (Cell("brand"), Cell("model"), Cell("engine"), Cell("ecu"));
            if (key.Item1 == "" || key.Item2 == "" || key.Item3 == "" || key.Item4 == "")
                errors.Add($"Line {i + 1}: brand, model, engine and ecu must all be filled in.");
            else if (key.Item1.Length > 40 || key.Item2.Length > 100 || key.Item3.Length > 100 || key.Item4.Length > 100)
                errors.Add($"Line {i + 1}: a text is too long (brand 40, others 100 characters).");
            else if (!Money.TryParseCents(Cell("price"), out var cents) || cents <= 0)
                errors.Add($"Line {i + 1}: \"{Cell("price")}\" is not a price.");
            else if (!rows.TryAdd(key, cents))
                errors.Add($"Line {i + 1}: the same vehicle and computer appears twice.");
        }
        if (rows.Count == 0 && errors.Count == 0) errors.Add("The file has no price lines.");
        if (errors.Count > 0) return new(0, 0, 0, errors.Take(20).ToList());

        int added = 0, updated = 0, deactivated = 0;
        var existing = await db.PriceList.ToListAsync(ct);
        foreach (var entry in existing)
        {
            if (rows.Remove((entry.Brand, entry.Model, entry.Engine, entry.Ecu), out var cents))
            {
                if (entry.PriceCents != cents || !entry.IsActive) updated++;
                entry.PriceCents = cents;
                entry.IsActive = true;
            }
            else if (entry.IsActive)
            {
                entry.IsActive = false;
                deactivated++;
            }
        }
        foreach (var (key, cents) in rows)
        {
            db.PriceList.Add(new PriceListEntry { Brand = key.Item1, Model = key.Item2, Engine = key.Item3, Ecu = key.Item4, PriceCents = cents });
            added++;
        }
        await db.SaveChangesAsync(ct); // one SaveChanges = one transaction: all lines or none
        return new(added, updated, deactivated, []);

        static PriceListImportResult Fail(string message) => new(0, 0, 0, [message]);
    }

    /// <summary>Simple split: our columns never contain the separator. Quotes around a cell (Excel adds them sometimes) are removed.</summary>
    private static List<string> Split(string line, char separator) =>
        line.Split(separator).Select(c => c.Trim().Trim('"').Trim()).ToList();
}
