using System;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Covers the channel-level year filter: an empty set allows every year, a non-empty set admits only items whose
/// production year is listed, and an item with no year is dropped while the filter is active.
/// </summary>
public class YearFilterTests
{
    [Fact]
    public void NullSet_IsInactive_AllowsEverything()
    {
        var filter = new ChannelService.YearFilter(null);

        Assert.False(filter.IsActive);
        Assert.True(filter.Allows(1999));
        Assert.True(filter.Allows(null));
    }

    [Fact]
    public void EmptySet_IsInactive_AllowsEverything()
    {
        var filter = new ChannelService.YearFilter(Array.Empty<int>());

        Assert.False(filter.IsActive);
        Assert.True(filter.Allows(2024));
        Assert.True(filter.Allows(null));
    }

    [Fact]
    public void NonEmptySet_AdmitsListedYears_DropsOthers()
    {
        var filter = new ChannelService.YearFilter(new[] { 1990, 1991, 1992, 1993, 1994, 1995, 1996, 1997, 1998, 1999 });

        Assert.True(filter.IsActive);
        Assert.True(filter.Allows(1990));
        Assert.True(filter.Allows(1999));
        Assert.False(filter.Allows(1989));
        Assert.False(filter.Allows(2000));
    }

    [Fact]
    public void NonEmptySet_DropsItemsWithNoYear()
    {
        var filter = new ChannelService.YearFilter(new[] { 1985, 1999, 2003 });

        Assert.False(filter.Allows(null));
        Assert.True(filter.Allows(1999));
        Assert.False(filter.Allows(1986));
    }

    [Fact]
    public void DuplicateYears_AreDeduplicated_AndStillMatch()
    {
        var filter = new ChannelService.YearFilter(new[] { 1977, 1977, 1977 });

        Assert.True(filter.IsActive);
        Assert.True(filter.Allows(1977));
        Assert.False(filter.Allows(1978));
    }
}
