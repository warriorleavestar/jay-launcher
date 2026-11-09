using Amber.Core.Launch;

namespace Amber.Core.Tests;

public class SettingInputTests
{
    [Theory]
    [InlineData("１９２０", 1920)]
    [InlineData(" 1080 ", 1080)]
    [InlineData("８５４", 854)]
    public void FullWidthDigitsAreRead(string text, int expected)
    {
        Assert.True(SettingInput.TryParsePositiveInt(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("12.5")]
    public void BadNumbersAreRejected(string text) => Assert.False(SettingInput.TryParsePositiveInt(text, out _));

    [Fact]
    public void BlankMeansUnset()
    {
        Assert.True(SettingInput.TryParsePositiveInt("  ", out var v));
        Assert.Null(v);
        Assert.True(SettingInput.TryParseMemoryMb("", out var m));
        Assert.Null(m);
    }

    [Fact]
    public void MaxIsEnforced() => Assert.False(SettingInput.TryParsePositiveInt("200", out _, max: 128));

    [Theory]
    [InlineData("4G", 4096)]
    [InlineData("4g", 4096)]
    [InlineData("4 GB", 4096)]
    [InlineData("4.5G", 4608)]
    [InlineData("4096", 4096)]
    [InlineData("4096M", 4096)]
    [InlineData("4096 mb", 4096)]
    [InlineData("４Ｇ", 4096)]
    public void MemoryFormsAreRead(string text, int expected)
    {
        Assert.True(SettingInput.TryParseMemoryMb(text, out var mb));
        Assert.Equal(expected, mb);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("100")]
    [InlineData("4T")]
    [InlineData("2000G")]
    public void BadMemoryIsRejected(string text) => Assert.False(SettingInput.TryParseMemoryMb(text, out _));

    [Theory]
    [InlineData("-Xmx4G -Dfoo=\"a b\"", true)]
    [InlineData("-Dfoo='a b'", true)]
    [InlineData("-Dfoo=\"it's\"", true)]
    [InlineData("-Dfoo=\\\"x", true)]
    [InlineData("-Dfoo=\"a b", false)]
    [InlineData("-Dfoo='a", false)]
    public void QuotesMustPair(string text, bool ok) => Assert.Equal(ok, SettingInput.QuotesBalanced(text));
}
