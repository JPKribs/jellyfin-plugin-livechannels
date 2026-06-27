using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="DefaultLogoService.FitTitleLines"/> — the generated-logo title fitting ladder:
/// one line, else two wrapped rows, else an initials abbreviation, else dropped.
/// </summary>
public class DefaultLogoTests
{
    [Fact]
    public void ShortTitle_FitsOnOneLine_Uppercased()
        => Assert.Equal(new[] { "CODEC TEST" }, DefaultLogoService.FitTitleLines("Codec Test"));

    [Fact]
    public void TitleAtLineLimit_StaysOneLine()
        => Assert.Equal(new[] { "COMEDY CHANNEL" }, DefaultLogoService.FitTitleLines("Comedy Channel"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrBlank_ProducesNoLines(string? name)
        => Assert.Empty(DefaultLogoService.FitTitleLines(name!));

    [Fact]
    public void LongMultiWord_WrapsToTwoRows()
    {
        var lines = DefaultLogoService.FitTitleLines("The Action Movie Network");
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 15));
        Assert.Equal("THE ACTION MOVIE NETWORK", string.Join(" ", lines));
    }

    [Fact]
    public void TooLongForTwoRows_FallsBackToInitials()
    {
        // Six words that cannot fit on two 18-char rows, but whose initials (6 chars) do.
        var lines = DefaultLogoService.FitTitleLines("Alpha Bravo Charlie Delta Echo Foxtrot");
        Assert.Equal(new[] { "ABCDEF" }, lines);
    }

    [Fact]
    public void SingleUnwrappableWord_IsDropped()
        => Assert.Empty(DefaultLogoService.FitTitleLines("Supercalifragilisticexpialidocious"));
}
