using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="SubtitleStyle"/>: the libass style override carries only what the viewer actually
/// customised, so an untouched configuration renders each subtitle exactly as authored.
/// </summary>
public class SubtitleStyleTests
{
    [Fact]
    public void NothingCustomised_ProducesNoOverride()
    {
        Assert.Null(SubtitleStyle.Build(string.Empty, 100, string.Empty, string.Empty, SubtitleBorderStyle.Default, bold: false));
    }

    [Fact]
    public void DefaultSizeAndBorder_AreNotEmitted()
    {
        var style = SubtitleStyle.Build("Arial", 100, string.Empty, string.Empty, SubtitleBorderStyle.Default, bold: false);

        Assert.Equal("FontName=Arial", style);
    }

    [Fact]
    public void SizeIsEmittedAsAMultiplier_NotAPercentage()
    {
        // libass divides a parsed Style line's ScaleX/ScaleY by 100 but stores a force_style override raw, so
        // "150%" has to be written as 1.5. Writing 150 would scale the glyphs 150 times over.
        Assert.Equal("ScaleX=1.5,ScaleY=1.5", SubtitleStyle.Build(null, 150, null, null, SubtitleBorderStyle.Default, bold: false));
        Assert.Equal("ScaleX=0.75,ScaleY=0.75", SubtitleStyle.Build(null, 75, null, null, SubtitleBorderStyle.Default, bold: false));
    }

    [Fact]
    public void OutOfRangeSize_IsIgnored()
    {
        Assert.Null(SubtitleStyle.Build(null, 5000, null, null, SubtitleBorderStyle.Default, bold: false));
        Assert.Null(SubtitleStyle.Build(null, 0, null, null, SubtitleBorderStyle.Default, bold: false));
    }

    [Fact]
    public void ColoursAreConvertedToAssByteOrder()
    {
        var style = SubtitleStyle.Build(null, 100, "#FFCC00", "#101820", SubtitleBorderStyle.Default, bold: false);

        // #RRGGBB becomes &H00BBGGRR.
        Assert.Equal("PrimaryColour=&H0000CCFF,OutlineColour=&H00201810", style);
    }

    [Theory]
    [InlineData("#FFFFFF", "&H00FFFFFF")]
    [InlineData("ffffff", "&H00FFFFFF")]
    [InlineData("  #010203  ", "&H00030201")]
    public void ColourAcceptsHashOptionalAndAnyCase(string input, string expected)
    {
        Assert.True(SubtitleStyle.TryConvertColor(input, out var ass));
        Assert.Equal(expected, ass);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFF")]
    [InlineData("not a colour")]
    [InlineData("#GGGGGG")]
    public void InvalidColourIsRejected(string? input)
    {
        Assert.False(SubtitleStyle.TryConvertColor(input, out _));
    }

    [Fact]
    public void BoxBorder_DrawsASolidBackgroundWithNoShadow()
    {
        var style = SubtitleStyle.Build(null, 100, null, null, SubtitleBorderStyle.Box, bold: false);

        Assert.Equal("BorderStyle=3,Outline=1,Shadow=0", style);
    }

    [Fact]
    public void OutlineBorderAndBold()
    {
        var style = SubtitleStyle.Build(null, 100, null, null, SubtitleBorderStyle.Outline, bold: true);

        Assert.Equal("BorderStyle=1,Outline=2,Shadow=1,Bold=-1", style);
    }

    [Fact]
    public void FontNameCannotBreakOutOfTheFilterArgument()
    {
        // Quotes, colons, commas, and backslashes would end the option, the entry, or the quoted filter string.
        var style = SubtitleStyle.Build("Ari'al::,\\x", 100, null, null, SubtitleBorderStyle.Default, bold: false);

        Assert.Equal("FontName=Arialx", style);
    }
}
