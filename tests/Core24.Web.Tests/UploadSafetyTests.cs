using System.Text;
using Core24.Web.Features.Orders;

namespace Core24.Web.Tests;

public class FileSignatureTests
{
    [Theory]
    [InlineData("MZ\x90\x00", "a Windows program")]
    [InlineData("PK\x03\x04", "a ZIP archive or Office document")]
    [InlineData("%PDF-1.7", "a PDF document")]
    [InlineData("  <html><body>", "a web page or script")]
    [InlineData("<!DOCTYPE html>", "a web page or script")]
    public void Recognises_files_that_are_not_ecu_dumps(string start, string expected) =>
        Assert.Equal(expected, FileSignature.DetectForbidden(Encoding.Latin1.GetBytes(start)));

    [Fact]
    public void Accepts_typical_dump_contents()
    {
        Assert.Null(FileSignature.DetectForbidden([0xFF, 0xFF, 0xFF, 0xFF, 0x12, 0x34]));   // erased flash
        Assert.Null(FileSignature.DetectForbidden([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        Assert.Null(FileSignature.DetectForbidden([]));                                       // empty: caught by the size check instead
    }
}

public class CleanFileNameTests
{
    [Theory]
    [InlineData(@"C:\Users\x\dump.bin", "dump.bin")]          // old browsers sent the full path
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("dump\r\nX-Evil: 1.bin", "dumpX-Evil: 1.bin")] // control characters removed
    [InlineData("   ", "file.bin")]
    public void Keeps_only_a_safe_name(string raw, string expected) => Assert.Equal(expected, OrderService.CleanFileName(raw));

    [Fact]
    public void Shortens_very_long_names_but_keeps_the_extension()
    {
        var name = OrderService.CleanFileName(new string('a', 500) + ".bin");
        Assert.Equal(200, name.Length);
        Assert.EndsWith(".bin", name);
    }
}
