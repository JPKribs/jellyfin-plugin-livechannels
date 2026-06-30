using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Covers the pure parts of the channel content filters added for 7.0: the community/critic rating floors and the
/// studio overlap. (Year filtering has its own tests; the people filter is a library query, exercised end to end.)
/// </summary>
public class ContentFilterTests
{
    [Theory]
    [InlineData(0.0, null, true)]    // floor off: anything passes, including a missing rating
    [InlineData(0.0, 3.0f, true)]
    [InlineData(7.5, null, false)]   // floor on: a missing rating is dropped
    [InlineData(7.5, 7.4f, false)]   // below the floor
    [InlineData(7.5, 7.5f, true)]    // exactly the floor
    [InlineData(7.5, 9.1f, true)]    // above the floor
    public void PassesMinRating_HonoursFloor(double minimum, float? value, bool expected)
    {
        Assert.Equal(expected, ChannelService.PassesMinRating(value, minimum));
    }

    [Fact]
    public void PassesStudios_EmptySet_AllowsEverything()
    {
        var channelStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ChannelService.PassesStudios(channelStudios, Array.Empty<string>()));
        Assert.True(ChannelService.PassesStudios(channelStudios, new[] { "HBO" }));
    }

    [Fact]
    public void PassesStudios_NonEmpty_RequiresOverlap_CaseInsensitive()
    {
        var channelStudios = new HashSet<string>(new[] { "HBO", "A24" }, StringComparer.OrdinalIgnoreCase);

        Assert.True(ChannelService.PassesStudios(channelStudios, new[] { "Warner Bros.", "hbo" }));
        Assert.True(ChannelService.PassesStudios(channelStudios, new[] { "a24" }));
        Assert.False(ChannelService.PassesStudios(channelStudios, new[] { "Netflix", "Disney" }));
    }

    [Fact]
    public void PassesStudios_NonEmpty_DropsItemsWithNoStudios()
    {
        var channelStudios = new HashSet<string>(new[] { "HBO" }, StringComparer.OrdinalIgnoreCase);

        Assert.False(ChannelService.PassesStudios(channelStudios, Array.Empty<string>()));
    }
}
