using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the wall-clock scheduling math.
/// </summary>
public class ScheduleCalculatorTests
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    private static ProgramEntry Program(string title, TimeSpan duration)
        => new ProgramEntry(Guid.NewGuid(), title, null, duration.Ticks, "/media/" + title + ".mkv");

    private static List<ProgramEntry> ThreeHourLoop()
        => new()
        {
            Program("A", TimeSpan.FromHours(1)),
            Program("B", TimeSpan.FromHours(1)),
            Program("C", TimeSpan.FromHours(1))
        };

    [Fact]
    public void CurrentProgram_EmptyList_ReturnsZero()
    {
        var (index, offset) = ScheduleCalculator.CurrentProgram(new List<ProgramEntry>(), Epoch, Epoch);

        Assert.Equal(0, index);
        Assert.Equal(TimeSpan.Zero, offset);
    }

    [Fact]
    public void CurrentProgram_PicksItemAndOffsetWithinCycle()
    {
        var programs = ThreeHourLoop();
        var now = Epoch.AddMinutes(90); // 30 min into the second item

        var (index, offset) = ScheduleCalculator.CurrentProgram(programs, now, Epoch);

        Assert.Equal(1, index);
        Assert.Equal(TimeSpan.FromMinutes(30), offset);
    }

    [Fact]
    public void CurrentProgram_WrapsAroundTheLoop()
    {
        var programs = ThreeHourLoop(); // 3h cycle
        var now = Epoch.AddHours(3).AddMinutes(15); // back to item A, 15 min in

        var (index, offset) = ScheduleCalculator.CurrentProgram(programs, now, Epoch);

        Assert.Equal(0, index);
        Assert.Equal(TimeSpan.FromMinutes(15), offset);
    }

    [Fact]
    public void BuildSchedule_FirstEntryStartsAtAiringItemStart_NotNow()
    {
        var programs = ThreeHourLoop();
        var now = Epoch.AddMinutes(90);

        var schedule = ScheduleCalculator.BuildSchedule(programs, now, now.AddHours(2), Epoch);

        Assert.NotEmpty(schedule);
        // The currently airing item (B) started 30 minutes before now.
        Assert.Equal(now.AddMinutes(-30), schedule[0].Start);
        Assert.Equal("B", schedule[0].Program.Title);
    }

    [Fact]
    public void BuildSchedule_ProgrammesAreContiguousAndCoverWindow()
    {
        var programs = ThreeHourLoop();
        var now = Epoch.AddMinutes(90);
        var end = now.AddHours(5);

        var schedule = ScheduleCalculator.BuildSchedule(programs, now, end, Epoch);

        for (var i = 1; i < schedule.Count; i++)
        {
            Assert.Equal(schedule[i - 1].Stop, schedule[i].Start);
        }

        Assert.True(schedule[^1].Stop >= end);
    }

    [Fact]
    public void BuildSchedule_EmptyWhenWindowInverted()
    {
        var schedule = ScheduleCalculator.BuildSchedule(ThreeHourLoop(), Epoch.AddHours(2), Epoch, Epoch);

        Assert.Empty(schedule);
    }
}
