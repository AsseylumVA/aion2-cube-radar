using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class RelativeHeightTests
{
    [Theory]
    [InlineData(3.01, "▲3")]
    [InlineData(12.4, "▲12")]
    [InlineData(3.6, "▲4")]
    [InlineData(-3.01, "▼3")]
    [InlineData(-4.6, "▼5")]
    [InlineData(-12.2, "▼12")]
    public void Arrow_shows_the_sign_and_whole_metres_outside_the_dead_zone(double metres, string text) =>
        Assert.Equal(text, RelativeHeight.Arrow(metres));

    [Theory]
    [InlineData(0d)]
    [InlineData(3d)]
    [InlineData(-3d)]
    [InlineData(2.9)]
    [InlineData(-2.9)]
    public void Arrow_is_blank_within_three_metres(double metres) =>
        Assert.Null(RelativeHeight.Arrow(metres));

    [Fact]
    public void Arrow_is_blank_when_the_height_is_unknown() =>
        Assert.Null(RelativeHeight.Arrow(null));

    [Fact]
    public void Metres_converts_centimetres_and_keeps_the_sign()
    {
        Assert.Equal(4, RelativeHeight.Metres(400));
        Assert.Equal(-3.5, RelativeHeight.Metres(-350));
        Assert.Null(RelativeHeight.Metres(null));
        Assert.Null(RelativeHeight.Arrow(RelativeHeight.Metres(300)));
        Assert.Equal("▲4", RelativeHeight.Arrow(RelativeHeight.Metres(350)));
        Assert.Equal("▼4", RelativeHeight.Arrow(RelativeHeight.Metres(-350)));
    }
}
