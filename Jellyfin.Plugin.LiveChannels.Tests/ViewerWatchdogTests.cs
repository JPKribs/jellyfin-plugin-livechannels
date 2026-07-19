using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the viewer watchdog's matcher: <see cref="LiveChannelsTvService.SelectUnwatchedConsumers"/> flags the
/// consumer ids no playback session references, matching both Jellyfin's prefixed live stream id form and a bare
/// media source id.
/// </summary>
public class ViewerWatchdogTests
{
    private const string ConsumerA = "lc_0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string ConsumerB = "lc_ffeeddccbbaa99887766554433221100";

    [Fact]
    public void PrefixedLiveStreamId_CountsAsWatched()
    {
        // Jellyfin reports our id behind an MD5 service prefix: {prefix}_{consumerId}.
        var reported = new List<string> { "9e0c1f2a3b4c5d6e7f8091a2b3c4d5e6_" + ConsumerA };

        Assert.Empty(LiveChannelsTvService.SelectUnwatchedConsumers(new[] { ConsumerA }, reported));
    }

    [Fact]
    public void BareMediaSourceId_CountsAsWatched()
    {
        var reported = new List<string> { ConsumerA };

        Assert.Empty(LiveChannelsTvService.SelectUnwatchedConsumers(new[] { ConsumerA }, reported));
    }

    [Fact]
    public void UnreportedConsumer_IsFlagged()
    {
        var reported = new List<string> { "prefix_" + ConsumerA };

        var unwatched = LiveChannelsTvService.SelectUnwatchedConsumers(new[] { ConsumerA, ConsumerB }, reported);

        Assert.Equal(new[] { ConsumerB }, unwatched);
    }

    [Fact]
    public void NoPlaybackSessions_FlagsEveryConsumer()
    {
        var unwatched = LiveChannelsTvService.SelectUnwatchedConsumers(new[] { ConsumerA, ConsumerB }, new List<string>());

        Assert.Equal(new[] { ConsumerA, ConsumerB }, unwatched);
    }

    [Fact]
    public void NoConsumers_FlagsNothing()
    {
        var reported = new List<string> { "prefix_" + ConsumerA };

        Assert.Empty(LiveChannelsTvService.SelectUnwatchedConsumers(new List<string>(), reported));
    }

    [Fact]
    public void EmptyReportedIds_AreIgnored()
    {
        var reported = new List<string> { string.Empty };

        var unwatched = LiveChannelsTvService.SelectUnwatchedConsumers(new[] { ConsumerA }, reported);

        Assert.Equal(new[] { ConsumerA }, unwatched);
    }
}
