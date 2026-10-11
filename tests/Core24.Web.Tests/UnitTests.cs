using Core24.Web;
using Core24.Web.Features.Orders;

namespace Core24.Web.Tests;

public class EcuFileNameTests
{
    [Fact]
    public void Parses_a_name_written_by_the_reading_tool()
    {
        var info = EcuFileName.TryParse("WDB9634031L927285_MCM2_PFLASH+DFLASH_564143F36EE2C882FA7CDC4D00D50E84_OBD.bin");

        Assert.NotNull(info);
        Assert.Equal("WDB9634031L927285", info.Vin);
        Assert.Equal("MCM2", info.EcuType);
        Assert.Equal("PFLASH+DFLASH", info.MemoryArea);
        Assert.Equal("564143F36EE2C882FA7CDC4D00D50E84", info.Md5);
        Assert.Equal("OBD", info.ReadMethod);
    }

    [Fact]
    public void Finds_the_pattern_inside_a_prefixed_name()
    {
        // The client's sample of an already-modified file looks like this.
        var info = EcuFileName.TryParse("arabdiag_mod_WDB9634031L927285_MCM2_PFLASH+DFLASH_564143F36EE2C882FA7CDC4D00D50E84_OBD.bin.bin");

        Assert.NotNull(info);
        Assert.Equal("WDB9634031L927285", info.Vin);
    }

    [Theory]
    [InlineData("my-truck.bin")]
    [InlineData("WDB9634031L927285_MCM2.bin")]                                            // no checksum
    [InlineData("WDB9634031L92728_MCM2_EEPROM_0ACF978186BE1B298382ED97F7256CF5_OBD.bin")] // VIN one char short
    public void Returns_null_for_other_names(string name) => Assert.Null(EcuFileName.TryParse(name));
}

public class VinTests
{
    [Theory]
    [InlineData("WDB9634031L927285", true)]
    [InlineData("WDB9634031L92728", false)]   // 16 characters
    [InlineData("WDB9634031LO27285", false)]  // O is never used in a VIN
    [InlineData("wdb9634031l927285", false)]  // we store VINs upper-case
    public void Validates_vins(string vin, bool valid) => Assert.Equal(valid, Vin.IsValid(vin));

    [Fact]
    public void Knows_the_maker_from_the_first_three_characters()
    {
        Assert.Equal("Mercedes-Benz", Vin.MakeFromWmi("WDB9634031L927285"));
        Assert.Null(Vin.MakeFromWmi("1FTFW1ET5DFC10312")); // a Ford pickup: not a truck make we list
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData(29000, "EUR", "€290.00")]
    [InlineData(150, "EUR", "€1.50")]
    [InlineData(123456, "EUR", "€1,234.56")]
    [InlineData(5000, "CHF", "50.00 CHF")]
    public void Formats_cents(long cents, string currency, string expected) => Assert.Equal(expected, Money.Format(cents, currency));

    [Theory]
    [InlineData("290", 29000)]
    [InlineData("290.5", 29050)]
    [InlineData("0.105", 11)] // rounds to whole cents
    public void Converts_to_cents(string amount, long expected) =>
        Assert.Equal(expected, Money.ToCents(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
}
