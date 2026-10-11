using System.Text.RegularExpressions;

namespace Core24.Web.Features.Orders;

/// <summary>What a reading tool wrote into the file name.</summary>
public sealed record EcuFileInfo(string Vin, string EcuType, string MemoryArea, string Md5, string ReadMethod);

/// <summary>
/// Reads names like <c>WDB9634031L927285_MCM2_PFLASH+DFLASH_564143F36EE2C882FA7CDC4D00D50E84_OBD.bin</c>:
/// VIN _ ECU _ memory area _ MD5 of the file _ read method.
/// </summary>
public static partial class EcuFileName
{
    // Not anchored to the start, so prefixed names ("arabdiag_mod_WDB...") still match.
    // VINs never contain I, O or Q, hence the gaps in the first character class.
    [GeneratedRegex(
        @"(?<vin>[A-HJ-NPR-Z0-9]{17})_(?<ecu>[A-Z0-9]+)_(?<mem>[A-Z0-9+]+)_(?<md5>[0-9A-F]{32})_(?<method>[A-Z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static EcuFileInfo? TryParse(string fileName)
    {
        var m = Pattern().Match(fileName);
        if (!m.Success) return null;

        return new EcuFileInfo(
            Vin: m.Groups["vin"].Value.ToUpperInvariant(),
            EcuType: m.Groups["ecu"].Value.ToUpperInvariant(),
            MemoryArea: m.Groups["mem"].Value.ToUpperInvariant(),
            Md5: m.Groups["md5"].Value.ToUpperInvariant(),
            ReadMethod: m.Groups["method"].Value.ToUpperInvariant());
    }

    /// <summary>Human description of the ECU codes we have seen so far.</summary>
    public static string DescribeEcu(string? ecuType) => ecuType switch
    {
        "MCM2" => "MCM2, engine control",
        "ACM2" => "ACM2, exhaust after-treatment (DPF, AdBlue/SCR)",
        null or "" => "Unknown",
        var other => other,
    };

    public static string DescribeMemory(string? memoryArea) => memoryArea switch
    {
        "PFLASH+DFLASH" => "PFLASH+DFLASH, program and calibration",
        "EEPROM" => "EEPROM, VIN and adaptation values",
        null or "" => "Unknown",
        var other => other,
    };

    public static string DescribeReadMethod(string? method) => method switch
    {
        "OBD" => "OBD, through the diagnostic port",
        "DC" => "DC, direct or bench connection",
        null or "" => "Unknown",
        var other => other,
    };
}

public static class Vin
{
    public static bool IsValid(string? vin) =>
        vin is { Length: 17 } && vin.All(c => char.IsAsciiDigit(c) || (char.IsAsciiLetterUpper(c) && c is not ('I' or 'O' or 'Q')));

    /// <summary>
    /// The first three characters (the World Manufacturer Identifier) name the maker.
    /// Only the truck makes listed on the site; anything else returns null.
    /// </summary>
    public static string? MakeFromWmi(string? vin)
    {
        if (!IsValid(vin)) return null;
        return vin![..3] switch
        {
            "WDB" => "Mercedes-Benz",
            "YV2" => "Volvo",
            "YS2" or "XLE" => "Scania",
            "WMA" => "MAN",
            "XLR" => "DAF",
            "ZCF" => "Iveco",
            "VF6" => "Renault Trucks",
            _ => null,
        };
    }
}
