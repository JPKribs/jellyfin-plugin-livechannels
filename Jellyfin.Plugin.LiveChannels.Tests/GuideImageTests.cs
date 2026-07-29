using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="GuideImages.Select"/>: a program offers both a portrait poster and a landscape image, so
/// clients that lay out posters and clients that lay out wide cards each render the right shape.
/// </summary>
public class GuideImageTests
{
    private static readonly ArtworkSet Nothing = new(null, null, null, null);

    [Fact]
    public void Movie_UsesItsOwnPosterAndThumb()
    {
        var own = new ArtworkSet("/movie/poster.jpg", "/movie/thumb.jpg", "/movie/fanart.jpg", "/movie/logo.png");

        var chosen = GuideImages.Select(own, Nothing, ownPrimaryIsLandscape: false);

        Assert.Equal("/movie/poster.jpg", chosen.Primary);
        Assert.Equal("/movie/thumb.jpg", chosen.Thumb);
        Assert.Equal("/movie/fanart.jpg", chosen.Backdrop);
        Assert.Equal("/movie/logo.png", chosen.Logo);
    }

    [Fact]
    public void Movie_WithNoThumb_FallsBackToItsBackdropForTheLandscapeSlot()
    {
        var own = new ArtworkSet("/movie/poster.jpg", null, "/movie/fanart.jpg", null);

        var chosen = GuideImages.Select(own, Nothing, ownPrimaryIsLandscape: false);

        Assert.Equal("/movie/poster.jpg", chosen.Primary);
        Assert.Equal("/movie/fanart.jpg", chosen.Thumb);
    }

    [Fact]
    public void Episode_TakesItsPosterFromTheSeriesAndKeepsItsOwnStillAsTheLandscape()
    {
        // An episode's own primary is a landscape still, so it must not end up in the poster slot.
        var own = new ArtworkSet("/ep/still.jpg", null, null, null);
        var series = new ArtworkSet("/series/poster.jpg", "/series/thumb.jpg", "/series/fanart.jpg", "/series/logo.png");

        var chosen = GuideImages.Select(own, series, ownPrimaryIsLandscape: true);

        Assert.Equal("/series/poster.jpg", chosen.Primary);
        Assert.Equal("/ep/still.jpg", chosen.Thumb);
        Assert.Equal("/series/fanart.jpg", chosen.Backdrop);
        Assert.Equal("/series/logo.png", chosen.Logo);
    }

    [Fact]
    public void Episode_PrefersItsOwnThumbOverItsStill()
    {
        var own = new ArtworkSet("/ep/still.jpg", "/ep/thumb.jpg", null, null);
        var series = new ArtworkSet("/series/poster.jpg", "/series/thumb.jpg", null, null);

        var chosen = GuideImages.Select(own, series, ownPrimaryIsLandscape: true);

        Assert.Equal("/ep/thumb.jpg", chosen.Thumb);
    }

    [Fact]
    public void Episode_WithNoSeriesPoster_StillFillsThePortraitSlotWithWhatItHas()
    {
        var own = new ArtworkSet("/ep/still.jpg", null, null, null);

        var chosen = GuideImages.Select(own, Nothing, ownPrimaryIsLandscape: true);

        Assert.Equal("/ep/still.jpg", chosen.Primary);
        Assert.Equal("/ep/still.jpg", chosen.Thumb);
    }

    [Fact]
    public void Episode_WithNoArtworkOfItsOwn_FallsBackToTheSeriesForEverySlot()
    {
        var series = new ArtworkSet("/series/poster.jpg", "/series/thumb.jpg", "/series/fanart.jpg", "/series/logo.png");

        var chosen = GuideImages.Select(Nothing, series, ownPrimaryIsLandscape: true);

        Assert.Equal("/series/poster.jpg", chosen.Primary);
        Assert.Equal("/series/thumb.jpg", chosen.Thumb);
        Assert.Equal("/series/fanart.jpg", chosen.Backdrop);
        Assert.Equal("/series/logo.png", chosen.Logo);
    }

    [Fact]
    public void NoArtworkAnywhere_LeavesEverySlotEmpty()
    {
        var chosen = GuideImages.Select(Nothing, Nothing, ownPrimaryIsLandscape: false);

        Assert.Null(chosen.Primary);
        Assert.Null(chosen.Thumb);
        Assert.Null(chosen.Backdrop);
        Assert.Null(chosen.Logo);
    }
}
