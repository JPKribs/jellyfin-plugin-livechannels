using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="TimerService"/>: timers survive a reload (a restart), cancels remove them, expired
/// windows age out, and re-scheduling a program replaces its timer instead of stacking a duplicate.
/// </summary>
public sealed class TimerServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "livechannels-timer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IApplicationPaths _paths;

    public TimerServiceTests()
    {
        _paths = Substitute.For<IApplicationPaths>();
        _paths.DataPath.Returns(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // A test that never persisted has nothing to clean up.
        }
    }

    [Fact]
    public void SaveTimer_AssignsId_AndSurvivesReload()
    {
        var id = NewService().SaveTimer(Timer("prog-1", InHours(1), InHours(2)));
        Assert.False(string.IsNullOrEmpty(id));

        // A fresh instance simulates a server restart reading the store back from disk.
        var reloaded = NewService().GetTimers();
        var timer = Assert.Single(reloaded);
        Assert.Equal(id, timer.Id);
        Assert.Equal("prog-1", timer.ProgramId);
        Assert.Equal(RecordingStatus.New, timer.Status);
    }

    [Fact]
    public void SaveTimer_ForSameProgram_ReplacesInsteadOfStacking()
    {
        var service = NewService();
        var first = service.SaveTimer(Timer("prog-1", InHours(1), InHours(2)));
        var second = service.SaveTimer(Timer("prog-1", InHours(1), InHours(2)));

        var timer = Assert.Single(service.GetTimers());
        Assert.Equal(second, timer.Id);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CancelTimer_LeavesATombstone_ThatBlocksRecreation()
    {
        var service = NewService();
        service.SaveTimer(Timer("prog-1", InHours(1), InHours(2)));
        var cancel = service.SaveTimer(Timer("prog-2", InHours(3), InHours(4)));

        service.CancelTimer(cancel);

        // The cancelled timer stays as a tombstone (so series expansion cannot re-create the airing), keeps
        // its status across reloads, and is never due for recording.
        var reloaded = NewService();
        var cancelled = reloaded.GetTimers().Single(t => t.ProgramId == "prog-2");
        Assert.Equal(RecordingStatus.Cancelled, cancelled.Status);
        Assert.True(reloaded.HasTimerForProgram("prog-2"));
        Assert.DoesNotContain(reloaded.TimersDueForRecording(), t => t.ProgramId == "prog-2");
    }

    [Fact]
    public void CancelDuringWindow_AbortsInPlace_NoRecordingDelivered()
    {
        var service = NewService();
        var id = service.SaveTimer(Timer("prog-1", InHours(-1), InHours(1)));

        service.CancelTimer(id);

        // Cancel aborts wherever the window stands: the timer becomes a tombstone and is never due for
        // materialization, so no recording is delivered.
        Assert.Empty(service.TimersDueForRecording());
        Assert.Equal(RecordingStatus.Cancelled, service.GetTimers().Single(t => t.Id == id).Status);
    }

    [Fact]
    public void CancelledTimer_AgesOutAfterWindow_AndRecordAgainReplacesIt()
    {
        var service = NewService();
        var expired = service.SaveTimer(Timer("prog-1", InHours(-2), InHours(-1)));
        service.CancelTimer(expired);
        Assert.Empty(service.GetTimers());

        var again = service.SaveTimer(Timer("prog-2", InHours(1), InHours(2)));
        service.CancelTimer(again);
        var replacement = service.SaveTimer(Timer("prog-2", InHours(1), InHours(2)));

        // Re-pressing record replaces the tombstone with a live timer via the per-program dedupe.
        var timer = Assert.Single(service.GetTimers());
        Assert.Equal(replacement, timer.Id);
        Assert.Equal(RecordingStatus.New, timer.Status);
    }

    [Fact]
    public void GetTimers_KeepsExpiredUntilMaterialized_AndMarksActiveInProgress()
    {
        var service = NewService();
        service.SaveTimer(Timer("ended", InHours(-3), InHours(-2)));
        service.SaveTimer(Timer("airing", InHours(-1), InHours(1)));
        service.SaveTimer(Timer("upcoming", InHours(1), InHours(2)));

        // The expired timer is NOT dropped: the recording service still owes it a materialization pass, and it
        // is the only one listed as due.
        var timers = service.GetTimers();
        Assert.Equal(3, timers.Count);
        Assert.Equal(RecordingStatus.InProgress, timers.Single(t => t.ProgramId == "airing").Status);
        Assert.Equal(RecordingStatus.New, timers.Single(t => t.ProgramId == "upcoming").Status);
        Assert.Equal("ended", Assert.Single(service.TimersDueForRecording()).ProgramId);
    }

    [Fact]
    public void CompleteTimer_SticksAndThenAgesOut()
    {
        var service = NewService();
        var id = service.SaveTimer(Timer("ended", InHours(-3), InHours(-2)));

        service.CompleteTimer(id, RecordingStatus.Completed, "/tmp/rec.mkv");

        // No longer due, and the terminal outcome survives a reload; the next read then ages it out of the
        // store entirely (the recording lives on as a library item).
        Assert.Empty(service.TimersDueForRecording());
        Assert.Empty(NewService().GetTimers());
        Assert.Empty(NewService().GetTimers());
    }

    [Fact]
    public void SaveSeriesTimer_ForSameProgram_ReplacesInsteadOfStacking()
    {
        var service = NewService();
        var first = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-1" });
        var second = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-1" });

        var rule = Assert.Single(service.GetSeriesTimers());
        Assert.Equal(second, rule.Id);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SaveSeriesTimer_Replace_RelinksChildren()
    {
        var service = NewService();
        var first = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-1" });
        var child = Timer("prog-2", InHours(1), InHours(2));
        child.SeriesTimerId = first;
        service.SaveTimer(child);

        var second = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-1" });

        // The replacement rule adopts the old rule's children, so no child dangles off a dead rule id.
        Assert.Equal(second, service.GetTimers().Single(t => t.ProgramId == "prog-2").SeriesTimerId);
    }

    [Fact]
    public void RelinkAndRemove_CollapseDuplicateRules_WithoutOrphans()
    {
        var service = NewService();
        var older = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-1" });
        var newer = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1", ProgramId = "prog-9" });
        var child = Timer("prog-2", InHours(1), InHours(2));
        child.SeriesTimerId = older;
        service.SaveTimer(child);

        service.RelinkSeriesChildren(older, newer);
        service.RemoveSeriesTimer(older);
        service.RemoveOrphanSeriesChildren(new[] { newer });

        Assert.Equal(newer, Assert.Single(service.GetSeriesTimers()).Id);
        Assert.Equal(newer, service.GetTimers().Single(t => t.ProgramId == "prog-2").SeriesTimerId);
    }

    [Fact]
    public void RemoveOrphanSeriesChildren_DropsDanglers_KeepsManualTimers()
    {
        var service = NewService();
        service.SaveTimer(Timer("manual", InHours(1), InHours(2)));
        var orphan = Timer("orphan", InHours(1), InHours(2));
        orphan.SeriesTimerId = "lc_timer_gone";
        service.SaveTimer(orphan);

        service.RemoveOrphanSeriesChildren(Array.Empty<string>());

        Assert.Equal("manual", Assert.Single(NewService().GetTimers()).ProgramId);
    }

    [Fact]
    public void HasTimerForProgram_SeesExistingTimers()
    {
        var service = NewService();
        service.SaveTimer(Timer("prog-1", InHours(1), InHours(2)));

        Assert.True(service.HasTimerForProgram("prog-1"));
        Assert.False(service.HasTimerForProgram("prog-2"));
    }

    [Fact]
    public void CancelSeriesTimer_RemovesItsTimersToo()
    {
        var service = NewService();
        var seriesId = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ChannelId = "chan-1" });
        var child = Timer("prog-1", InHours(1), InHours(2));
        child.SeriesTimerId = seriesId;
        service.SaveTimer(child);
        service.SaveTimer(Timer("prog-2", InHours(1), InHours(2)));

        service.CancelSeriesTimer(seriesId);

        Assert.Empty(service.GetSeriesTimers());
        Assert.Equal("prog-2", Assert.Single(service.GetTimers()).ProgramId);
    }

    [Fact]
    public void NewTimerDefaults_SeedsFromProgram()
    {
        var program = new ProgramInfo
        {
            Id = "chan-1_638000000000000000",
            ChannelId = "chan-1",
            Name = "Pilot",
            Overview = "An alien lives in the attic.",
            StartDate = new DateTime(2026, 7, 22, 20, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 7, 22, 20, 30, 0, DateTimeKind.Utc),
            SeriesId = "series-1"
        };

        var defaults = TimerService.NewTimerDefaults(program);
        Assert.Equal(program.Id, defaults.ProgramId);
        Assert.Equal(program.ChannelId, defaults.ChannelId);
        Assert.Equal(program.Name, defaults.Name);
        Assert.Equal(program.StartDate, defaults.StartDate);
        Assert.Equal(program.EndDate, defaults.EndDate);
        Assert.Equal(new List<DayOfWeek> { DayOfWeek.Wednesday }, defaults.Days);

        // Without a program the dialog still gets a usable blank: any time, every day.
        var blank = TimerService.NewTimerDefaults(null);
        Assert.True(blank.RecordAnyTime);
        Assert.Equal(7, blank.Days.Count);
    }

    private static DateTime InHours(int hours) => DateTime.UtcNow.AddHours(hours);

    private static TimerInfo Timer(string programId, DateTime start, DateTime end) => new()
    {
        ChannelId = "chan-1",
        ProgramId = programId,
        Name = "Show",
        StartDate = start,
        EndDate = end
    };

    private TimerService NewService() => new(_paths, NullLogger<TimerService>.Instance);
}
