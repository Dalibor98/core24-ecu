namespace Core24.Web.Features.Catalog;

/// <summary>
/// One line of the fixed price list: a modification the workshop offers.
/// Mods are never deleted, only deactivated, so old orders can still point at them.
/// </summary>
public class Mod
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Price in cents (29000 = 290.00). Money is never stored as double.</summary>
    public long PriceCents { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
