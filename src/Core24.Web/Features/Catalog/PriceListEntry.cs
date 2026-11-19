namespace Core24.Web.Features.Catalog;

/// <summary>
/// One line of the workshop's price list: a vehicle, its engine computer, and the price of
/// that computer's ORIGINAL (factory) file. Customers pick a line instead of typing their vehicle.
/// Lines are never deleted, only deactivated, so old orders can still point at them.
/// </summary>
public class PriceListEntry
{
    public int Id { get; set; }

    public required string Brand { get; set; }   // "Scania"
    public required string Model { get; set; }   // "DC13 Series G/P/R/T E5"
    public required string Engine { get; set; }  // "12.7TDI 380-480hp"

    /// <summary>The computer(s) the file is for, e.g. "Motorola EMS S6/ACM DCU15". This decides which file fits.</summary>
    public required string Ecu { get; set; }

    /// <summary>Price in cents (32000 = 320.00). Money is never stored as double.</summary>
    public long PriceCents { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>"Scania DC13 Series G/P/R/T E5 · 12.7TDI 380-480hp · Motorola EMS S6/ACM DCU15"</summary>
    public string Description => $"{Brand} {Model} · {Engine} · {Ecu}";
}
