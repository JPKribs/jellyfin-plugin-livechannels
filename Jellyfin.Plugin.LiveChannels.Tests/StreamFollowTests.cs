using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Controller.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for how a running stream session follows its schedule: picking up a rebuilt loop at an item boundary,
/// joining a time-of-day schedule after the tune-in card, and carrying the channel timeline forward exactly.
/// </summary>
public sealed class StreamFollowTests : IDisposable
{
    private readonly string _dir;

    public StreamFollowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lc-follow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LoopSchedule_ReanchorsOntoARebuiltLoop_AtTheNextBoundary()
    {
        // Airing item 1 of the old loop, joined 10 minutes in. The rebuild keeps the same timing but a new order.
        var old = new[] { Entry(0), Entry(1), Entry(2) };
        var rebuilt = new[] { Entry(7), Entry(8), Entry(9) };
        var (index, offset) = ScheduleCalculator.CurrentProgram(old, Join, ScheduleCalculator.Epoch);
        var follower = new ProgramsFollower(new Channel(), old, () => (new Channel(), rebuilt));
        var schedule = new LoopStreamSchedule(old, index, offset, Join, follower);

        Assert.Same(old[index], schedule.Current.Program);
        schedule.Advance();

        // The next item comes from the rebuilt loop, at what it airs when the old item ended (a boundary, so no seek).
        var boundary = Join - offset + TimeSpan.FromMinutes(30);
        var (expected, _) = ScheduleCalculator.CurrentProgram(rebuilt, boundary, ScheduleCalculator.Epoch);
        Assert.Same(rebuilt[expected], schedule.Current.Program);
        Assert.Equal(TimeSpan.Zero, schedule.Current.Offset);
        Assert.Same(rebuilt, follower.Programs);
    }

    [Fact]
    public void LoopSchedule_KeepsItsOrder_WhenTheLoopIsUnchanged()
    {
        var programs = new[] { Entry(0), Entry(1), Entry(2) };
        var follower = new ProgramsFollower(new Channel(), programs, () => (new Channel(), programs));
        var schedule = new LoopStreamSchedule(programs, 2, TimeSpan.FromMinutes(5), Join, follower);

        schedule.Advance();
        Assert.Same(programs[0], schedule.Current.Program);
        Assert.Equal(TimeSpan.Zero, schedule.Current.Offset);
    }

    [Fact]
    public void LoopSchedule_IgnoresAGoneChannel()
    {
        var programs = new[] { Entry(0), Entry(1) };
        var follower = new ProgramsFollower(new Channel(), programs, () => null);
        var schedule = new LoopStreamSchedule(programs, 0, TimeSpan.Zero, Join, follower);

        schedule.Advance();
        Assert.Same(programs[1], schedule.Current.Program);
    }

    [Fact]
    public void Snap_TreatsNearBoundaryPositionsAsTheBoundary()
    {
        var programs = new[] { Entry(0), Entry(1) };
        Assert.Equal((1, TimeSpan.Zero), StreamScheduleMath.Snap(programs, 0, TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1)));
        Assert.Equal((0, TimeSpan.Zero), StreamScheduleMath.Snap(programs, 0, TimeSpan.FromSeconds(1)));
        Assert.Equal((0, TimeSpan.FromMinutes(3)), StreamScheduleMath.Snap(programs, 0, TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void DaypartSchedule_JoinsWhereTheTuneInCardEnds()
    {
        // The first item's offset is measured at the join time handed in (now plus the card), not at plain "now",
        // so content lands on the wall clock the guide shows once the card ends.
        var pool = new[] { Entry(0), Entry(1), Entry(2) };
        var join = Join + TimeSpan.FromSeconds(9.5);
        var schedule = new DaypartStreamSchedule(NewChannelService(), new Channel { Id = "c", Number = 3 }, pool, join);

        var (index, offset) = ScheduleCalculator.CurrentProgram(pool, join, ScheduleCalculator.Epoch);
        Assert.Same(pool[index], schedule.Current.Program);
        Assert.Equal(offset, schedule.Current.Offset);
    }

    [Fact]
    public void DaypartSchedule_JoinsARebuiltLoop_AtTheBoundary()
    {
        var pool = new[] { Entry(0), Entry(1), Entry(2) };
        var rebuilt = new[] { Entry(7), Entry(8), Entry(9) };
        var channel = new Channel { Id = "c", Number = 3 };
        var follower = new ProgramsFollower(channel, pool, () => (channel, rebuilt));
        var schedule = new DaypartStreamSchedule(NewChannelService(), channel, pool, Join, follower);

        schedule.Advance();
        var (index, offset) = ScheduleCalculator.CurrentProgram(pool, Join, ScheduleCalculator.Epoch);
        var boundary = Join - offset + TimeSpan.FromMinutes(30);
        var (expected, _) = ScheduleCalculator.CurrentProgram(rebuilt, boundary, ScheduleCalculator.Epoch);
        Assert.Same(rebuilt[expected], schedule.Current.Program);
        Assert.Equal(TimeSpan.Zero, schedule.Current.Offset);
        Assert.NotEqual(-1, index);
    }

    [Fact]
    public void SpeedTracker_PlacesReadingsOnTheChannelTimeline()
    {
        // ffmpeg's out_time counts from the producer's own first output, excluding -output_ts_offset.
        var stats = new SessionStats();
        var tracker = new StreamSessionService.SpeedTracker(baseSeconds: 600);
        tracker.Observe("out_time_us=N/A", stats);
        Assert.Equal(-1, tracker.LastContentSeconds);

        tracker.Observe("out_time_us=1500000", stats);
        Assert.Equal(601.5, tracker.LastContentSeconds, 6);
        Assert.Equal(601.5 + (1.0 / 30.0), stats.TimelineSeconds, 6);

        // Without a stats sink the reading is still tracked (the timeline chains from it).
        var bare = new StreamSessionService.SpeedTracker(10);
        bare.Observe("out_time_us=2000000", null);
        Assert.Equal(12, bare.LastContentSeconds, 6);
    }

    [Fact]
    public void SessionStats_TimelineEndsAfterTheLastFrame_AndTheAnchorIsSetOnce()
    {
        var stats = new SessionStats();
        Assert.Equal(0, stats.TimelineSeconds);
        Assert.Null(stats.ContentAnchorUtc);

        var first = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(first, stats.SetContentAnchorOnce(first));
        Assert.Equal(first, stats.SetContentAnchorOnce(first.AddHours(1)));
        Assert.Equal(first, stats.ContentAnchorUtc);
    }

    [Fact]
    public void NextTimeline_RejectsAReadingThatDoubleCountsTheTimeline()
    {
        // A build that reported -output_ts_offset in out_time on top of the base would land near 2x the timeline.
        var timeline = TimeSpan.FromSeconds(7200);
        var advance = TimeSpan.FromSeconds(1800);
        Assert.Equal(timeline + advance, StreamSessionService.NextTimeline(timeline, advance, 7200 + 7200 + 1800));
        Assert.Equal(9000.5 + (1.0 / 30.0), StreamSessionService.NextTimeline(timeline, advance, 9000.5).TotalSeconds, 3);
    }

    [Fact]
    public void ConcatStream_CloseSummaryNamesTheLastSegmentServed()
    {
        File.WriteAllText(Path.Combine(_dir, "seg4.ts"), "AAA");
        File.WriteAllText(Path.Combine(_dir, "seg5.ts"), "BBB");
        var log = new List<string>();
        using (var stream = new SegmentConcatStream(_dir, log.Add, startBehind: 1))
        {
            var buffer = new byte[16];
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        Assert.Contains(log, l => l.Contains("seg4..seg5 ", StringComparison.Ordinal));
    }

    private static DateTime Join => new(2026, 3, 1, 20, 10, 0, DateTimeKind.Utc);

    private ChannelService NewChannelService()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_dir);
        return new ChannelService(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            NullLogger<ChannelService>.Instance);
    }

    private static ProgramEntry Entry(int index)
        => new(
            new Guid($"00000000-0000-0000-0000-{index:D12}"),
            "item" + index.ToString(CultureInfo.InvariantCulture),
            null,
            TimeSpan.FromMinutes(30).Ticks,
            "/media/item" + index.ToString(CultureInfo.InvariantCulture));
}
