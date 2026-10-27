using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// A video-only item is given a silent stereo track exactly as long as the video it plays, so the channel's audio
/// never has a hole (no packets) or an overlap at the item boundary.
/// </summary>
public class SilentAudioTests
{
    private static readonly VideoEncoderProfile Software =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private static ProgramEntry Item(double seconds, bool videoOnly)
        => new(Guid.NewGuid(), "Item", null, TimeSpan.FromSeconds(seconds).Ticks, "/m.mkv") { IsVideoOnly = videoOnly };

    [Fact]
    public void ItemWithAudio_GetsNoSilence()
        => Assert.Null(StreamSessionService.SilenceFor(Item(600, videoOnly: false), TimeSpan.Zero, null));

    [Fact]
    public void VideoOnly_GetsTheRestOfTheItem()
        => Assert.Equal(TimeSpan.FromSeconds(450), StreamSessionService.SilenceFor(Item(600, videoOnly: true), TimeSpan.FromSeconds(150), null));

    [Fact]
    public void VideoOnly_IsCappedByTheRunLimit()
        => Assert.Equal(TimeSpan.FromSeconds(30), StreamSessionService.SilenceFor(Item(600, videoOnly: true), TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(30)));

    [Fact]
    public void VideoOnly_OfUnknownLength_GetsNoEndlessSilence()
        => Assert.Null(StreamSessionService.SilenceFor(Item(0, videoOnly: true), TimeSpan.Zero, null));

    [Fact]
    public void Build_WithSilence_MapsABoundedLavfiTrack()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, Software, "aac", 192, null, silentAudio: TimeSpan.FromSeconds(42.5));
        var lavfi = a.IndexOf("anullsrc=channel_layout=stereo:sample_rate=48000");
        Assert.True(lavfi > a.IndexOf("/m.mkv"), "the silence is the second input");
        Assert.Equal("-t", a[lavfi - 5]);
        Assert.Equal("42.500", a[lavfi - 4]);
        Assert.Contains("1:a:0", a);
        Assert.DoesNotContain("0:a:0?", a);
        Assert.DoesNotContain("-shortest", a);
    }

    [Fact]
    public void Build_WithoutSilence_MapsTheItemsOwnAudio()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, Software, "aac", 192, null);
        Assert.DoesNotContain(a, x => x.Contains("anullsrc", StringComparison.Ordinal));
        Assert.Contains("0:a:0?", a);
    }
}
