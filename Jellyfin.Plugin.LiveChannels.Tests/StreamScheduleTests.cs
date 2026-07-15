using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="LoopStreamSchedule"/> -- the free-running item source the per-item stream loop uses for
/// ordinary channels. It must be byte-for-byte the old behaviour: play from the tune-in index and offset, then
/// each subsequent item in order (offset 0), wrapping, with no playback cap.
/// </summary>
public class StreamScheduleTests
{
    [Fact]
    public void LoopStreamSchedule_PlaysModularOrderWithFirstOffsetThenZero()
    {
        var programs = new[] { Entry(0), Entry(1), Entry(2) };
        var schedule = new LoopStreamSchedule(programs, startIndex: 1, startOffset: TimeSpan.FromSeconds(30));

        Assert.Equal(3, schedule.PoolCount);

        var first = schedule.Current;
        Assert.Equal(programs[1].ItemId, first.Program.ItemId);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Offset);
        Assert.Null(first.DurationLimit);

        schedule.Advance();
        var second = schedule.Current;
        Assert.Equal(programs[2].ItemId, second.Program.ItemId);
        Assert.Equal(TimeSpan.Zero, second.Offset);
        Assert.Null(second.DurationLimit);

        schedule.Advance();
        var third = schedule.Current;
        Assert.Equal(programs[0].ItemId, third.Program.ItemId); // wrapped
        Assert.Equal(TimeSpan.Zero, third.Offset);
    }

    private static ProgramEntry Entry(int index)
        => new(
            new Guid($"00000000-0000-0000-0000-{index:D12}"),
            "item" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            TimeSpan.FromMinutes(30).Ticks,
            "/media/item" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
