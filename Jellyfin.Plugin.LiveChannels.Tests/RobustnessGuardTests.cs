using System;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

public class RobustnessGuardTests
{
    [Fact]
    public void DolbyVisionProfile5_IsDetected_AndOtherProfilesAreNot()
    {
        // Profile 5 has no HDR10-compatible base layer and renders green/purple through any tone mapper;
        // it is the only DV flavour excluded from schedules.
        Assert.True(ChannelService.IsDolbyVisionProfile5(new MediaStream { DvProfile = 5 }));
        Assert.False(ChannelService.IsDolbyVisionProfile5(new MediaStream { DvProfile = 8 }));
        Assert.False(ChannelService.IsDolbyVisionProfile5(new MediaStream { DvProfile = 7 }));
        Assert.False(ChannelService.IsDolbyVisionProfile5(new MediaStream())); // no DV metadata
        Assert.False(ChannelService.IsDolbyVisionProfile5(null)); // no video stream
    }

    [Fact]
    public void ObservedItemSeconds_SubtractsTheTimelineBase()
    {
        // out_time includes the -output_ts_offset base: an item at timeline 3600s whose last progress block
        // says 5100.5s actually played 1500.5s of content.
        var observed = StreamSessionService.ObservedItemSeconds(5100.5, TimeSpan.FromSeconds(3600));
        Assert.NotNull(observed);
        Assert.Equal(1500.5, observed!.Value, 3);
    }

    [Fact]
    public void NextTimeline_ChainsFromTheActualEnd_PlusOneFrame()
    {
        // The item was scheduled for 1746.341s but actually ended at 1746.200s: the next item starts one
        // output frame after the real end, so the seam carries no PTS jump for the decoder to stall on.
        var next = StreamSessionService.NextTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(1746.341), 1746.200);
        Assert.Equal(1746.200 + (1.0 / 30.0), next.TotalSeconds, 3);

        // Also when the file ran LONGER than its metadata (the overlap case that regresses timestamps).
        var longer = StreamSessionService.NextTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(1700), 1746.200);
        Assert.Equal(1746.200 + (1.0 / 30.0), longer.TotalSeconds, 3);
    }

    [Fact]
    public void NextTimeline_FallsBackToTheSchedule_ForUnusableReadings()
    {
        var timeline = TimeSpan.FromSeconds(5000);
        var advance = TimeSpan.FromSeconds(1200);
        var scheduled = timeline + advance;

        // No reading parsed (no stats sink, or the producer never reported progress).
        Assert.Equal(scheduled, StreamSessionService.NextTimeline(timeline, advance, -1));
        // A reading that never moved past the current timeline (an instantly-dead producer).
        Assert.Equal(scheduled, StreamSessionService.NextTimeline(timeline, advance, 4000));
        // A garbage timestamp must never fling the channel timeline.
        Assert.Equal(scheduled, StreamSessionService.NextTimeline(timeline, advance, 5000 + 90000));
    }

    [Fact]
    public void ObservedItemSeconds_RejectsUnusableReadings()
    {
        // No progress parsed at all.
        Assert.Null(StreamSessionService.ObservedItemSeconds(-1, TimeSpan.Zero));
        // Too short to be a real item (a producer that died at once, or a reading behind the timeline base).
        Assert.Null(StreamSessionService.ObservedItemSeconds(5, TimeSpan.Zero));
        Assert.Null(StreamSessionService.ObservedItemSeconds(3600, TimeSpan.FromSeconds(3595)));
        // Longer than any real item; a garbage timestamp must not poison the schedule.
        Assert.Null(StreamSessionService.ObservedItemSeconds(90000, TimeSpan.Zero));
    }
}
