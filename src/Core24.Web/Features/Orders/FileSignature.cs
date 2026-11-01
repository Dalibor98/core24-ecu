using System.Text;

namespace Core24.Web.Features.Orders;

/// <summary>
/// An ECU dump is raw bytes with no fixed header, so we can't prove a file IS a dump.
/// We can prove it is NOT one: programs, archives and documents start with well-known
/// "magic bytes". A renamed virus.exe → dump.bin is caught here, before the workshop opens it.
/// </summary>
public static class FileSignature
{
    private static readonly (string Name, byte[] Magic)[] Known =
    [
        ("a Windows program", "MZ"u8.ToArray()),
        ("a Linux program", [0x7F, (byte)'E', (byte)'L', (byte)'F']),
        ("a ZIP archive or Office document", [(byte)'P', (byte)'K', 0x03, 0x04]),
        ("a RAR archive", "Rar!"u8.ToArray()),
        ("a 7-Zip archive", [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C]),
        ("a GZIP archive", [0x1F, 0x8B]),
        ("a PDF document", "%PDF"u8.ToArray()),
        ("an old Office document", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),
        ("a script", "#!"u8.ToArray()),
    ];

    /// <summary>Returns what the file really is (e.g. "a Windows program"), or null if it isn't a known non-ECU type.</summary>
    public static string? DetectForbidden(ReadOnlySpan<byte> start)
    {
        foreach (var (name, magic) in Known)
            if (start.StartsWith(magic)) return name;

        // Web pages and scripts as text: dangerous if anyone ever opens them in a browser.
        var text = Encoding.ASCII.GetString(start[..Math.Min(start.Length, 64)]).TrimStart().ToLowerInvariant();
        if (text.StartsWith("<html") || text.StartsWith("<!doctype") || text.StartsWith("<script") || text.StartsWith("<?xml") || text.StartsWith("<svg"))
            return "a web page or script";

        return null;
    }

    /// <summary>Reads the first bytes of an upload and puts the stream back to the start.</summary>
    public static string? DetectForbidden(IFormFile file)
    {
        Span<byte> buffer = stackalloc byte[64];
        using var stream = file.OpenReadStream();
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return DetectForbidden(buffer[..read]);
    }
}
