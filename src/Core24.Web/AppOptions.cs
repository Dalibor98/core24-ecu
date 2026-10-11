using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Core24.Web;

/// <summary>Site-wide settings, bound from the "App" section of appsettings.json.</summary>
public class AppOptions
{
    public const string Section = "App";

    /// <summary>The address customers use, e.g. "https://core24.eu". Needed to put links into emails.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>ISO currency code for the price list and payments.</summary>
    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "EUR";

    public string Link(string path) => PublicBaseUrl.TrimEnd('/') + path;
}

public static class Money
{
    /// <summary>29000, "EUR" → "€290.00"</summary>
    public static string Format(long cents, string currency)
    {
        var amount = (cents / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return currency.ToUpperInvariant() switch
        {
            "EUR" => "€" + amount,
            "USD" => "$" + amount,
            "GBP" => "£" + amount,
            var code => $"{amount} {code}",
        };
    }

    /// <summary>"290.50" (as typed by the admin) → 29050. Rounds to whole cents.</summary>
    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
