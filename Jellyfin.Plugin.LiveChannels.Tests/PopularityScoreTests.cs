using System;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the built-in Popular channel's ranking: <see cref="ChannelService.PopularityScore"/> blends
/// community rating with a recency boost so both well-liked and brand-new content surface.
/// </summary>
public class PopularityScoreTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HigherRating_RanksAhead_AtTheSameAge()
    {
        var aged = Now.AddDays(-1000);
        Assert.True(ChannelService.PopularityScore(9f, aged, Now) > ChannelService.PopularityScore(5f, aged, Now));
    }

    [Fact]
    public void NewerContent_RanksAhead_AtTheSameRating()
    {
        // Recency is the tie-breaker: a brand-new item beats an old one with the identical rating.
        Assert.True(ChannelService.PopularityScore(7f, Now, Now) > ChannelService.PopularityScore(7f, Now.AddDays(-1000), Now));
    }

    [Fact]
    public void UnratedContent_TreatedAsZero_StillOrdersByRecency()
    {
        Assert.True(ChannelService.PopularityScore(null, Now, Now) > ChannelService.PopularityScore(null, Now.AddDays(-1000), Now));
        Assert.True(ChannelService.PopularityScore(null, Now.AddDays(-1000), Now) >= 0);
    }

    [Fact]
    public void RecencyBoost_IsThreeWhenNew_AndZeroPastTheWindow()
    {
        Assert.Equal(10.0, ChannelService.PopularityScore(7f, Now, Now), 3);
        Assert.Equal(7.0, ChannelService.PopularityScore(7f, Now.AddDays(-1000), Now), 3);
    }
}
