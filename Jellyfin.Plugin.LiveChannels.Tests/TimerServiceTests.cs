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
    public void CancelTimer_RemovesIt_AcrossReloads()
    {
        var service = NewService();
        var keep = service.SaveTimer(Timer("prog-1", InHours(1), InHours(2)));
        var cancel = service.SaveTimer(Timer("prog-2", InHours(3), InHours(4)));

        service.CancelTimer(cancel);

        Assert.Equal(keep, Assert.Single(NewService().GetTimers()).Id);
    }

    [Fact]
    public void GetTimers_DropsExpired_AndMarksActiveInProgress()
    {
        var service = NewService();
        service.SaveTimer(Timer("ended", InHours(-3), InHours(-2)));
        service.SaveTimer(Timer("airing", InHours(-1), InHours(1)));
        service.SaveTimer(Timer("upcoming", InHours(1), InHours(2)));

        var timers = service.GetTimers();
        Assert.Equal(2, timers.Count);
        Assert.Equal(RecordingStatus.InProgress, timers.Single(t => t.ProgramId == "airing").Status);
        Assert.Equal(RecordingStatus.New, timers.Single(t => t.ProgramId == "upcoming").Status);

        // The expired timer is gone from the persisted store too, not just this read.
        Assert.Equal(2, NewService().GetTimers().Count);
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
