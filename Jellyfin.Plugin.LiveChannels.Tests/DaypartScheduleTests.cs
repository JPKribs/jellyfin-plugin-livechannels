using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="DaypartSchedule"/>: content is placed under the rating window active at its start time,
/// the chain is deterministic from the anchor, nothing is truncated at midnight, and the guide and stream
/// (which call it with different ranges) agree.
/// </summary>
public class DaypartScheduleTests
{
    private const int Pg = 100;
    private const int R = 400;

    // Daytime 06:00-20:00 caps at PG; the rest of the day caps at R.
    private static readonly ResolvedRatingBlock[] Blocks =
    {
        new(new RatingWindow(null, Pg, true), AllDay: false, 6 * 60, 20 * 60),
        new(new RatingWindow(null, R, true), AllDay: false, 20 * 60, 6 * 60)
    };

    // A pool of one-hour items across a spread of ratings.
    private static readonly ProgramEntry[] Loop = BuildLoop();

    private static readonly DateTime Midnight = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_PlacesEveryItemUnderItsStartWindow()
    {
        var schedule = DaypartSchedule.Build(Loop, Blocks, transitionMinutes: 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(1), "chan");

        Assert.NotEmpty(schedule);
        foreach (var slot in schedule)
        {
            var minute = (slot.Start.Hour * 60) + slot.Start.Minute; // tz is UTC, so wall-clock == local
            var window = RatingSchedule.EffectiveWindow(Blocks, minute);
            Assert.True(
                window.Allows(slot.Program.ParentalRatingValue),
                $"{slot.Program.Title} (rating {slot.Program.ParentalRatingValue}) aired at minute {minute} under a window that disallows it");
        }
    }

    [Fact]
    public void Build_DaytimeNeverExceedsPg()
    {
        var schedule = DaypartSchedule.Build(Loop, Blocks, transitionMinutes: 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(1), "chan");

        foreach (var slot in schedule.Where(s => s.Start.Hour is >= 6 and < 20))
        {
            Assert.True(slot.Program.ParentalRatingValue <= Pg, $"{slot.Program.Title} exceeded PG during the daytime window");
        }
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(2), "chan");
        var b = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(2), "chan");

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Program.ItemId, b[i].Program.ItemId);
            Assert.Equal(a[i].Start, b[i].Start);
            Assert.Equal(a[i].Stop, b[i].Stop);
        }
    }

    [Fact]
    public void Build_GuideAndStreamAgreeOnTheOverlap()
    {
        // The guide asks for a wide window; the stream tunes in mid-way. Both simulate the same chain from the
        // same anchor, so every programme the later call produces must match the wider call exactly.
        var wide = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(2), "chan");
        var later = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight.AddHours(30), Midnight.AddDays(2), "chan");

        Assert.NotEmpty(later);
        foreach (var slot in later)
        {
            Assert.Contains(wide, w => w.Program.ItemId == slot.Program.ItemId && w.Start == slot.Start && w.Stop == slot.Stop);
        }
    }

    [Fact]
    public void Build_DoesNotTruncateAtMidnight()
    {
        // 100-minute items do not tile a 24h day evenly, so some item must cross midnight; it airs in full.
        var loop = new[] { Entry(0, Pg, minutes: 100) };
        var schedule = DaypartSchedule.Build(loop, new[] { new ResolvedRatingBlock(new RatingWindow(null, R, true), false, 0, 12 * 60) }, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddDays(2), "chan");

        Assert.All(schedule, s => Assert.Equal(TimeSpan.FromMinutes(100), s.Stop - s.Start));
        Assert.Contains(schedule, s => s.Start < Midnight.AddDays(1) && s.Stop > Midnight.AddDays(1));
    }

    [Fact]
    public void Build_IsContiguousFromLocalMidnightOfTheAnchorDay()
    {
        // A mid-afternoon save anchors the chain at that day's local midnight, and the chain has no gaps.
        var anchor = Midnight.AddHours(14).AddMinutes(37);
        var schedule = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, anchor, Midnight, Midnight.AddDays(1), "chan");

        Assert.NotEmpty(schedule);
        Assert.Equal(Midnight, schedule[0].Start);
        for (var i = 1; i < schedule.Count; i++)
        {
            Assert.Equal(schedule[i - 1].Stop, schedule[i].Start);
        }
    }

    [Fact]
    public void Build_ReturnsNothingBeforeTheAnchorDay()
    {
        var anchor = Midnight.AddDays(5);
        var schedule = DaypartSchedule.Build(Loop, Blocks, 0, TimeZoneInfo.Utc, anchor, Midnight, Midnight.AddDays(1), "chan");

        Assert.Empty(schedule);
    }

    private static ProgramEntry[] BuildLoop()
    {
        var ratings = new int?[] { 50, Pg, 200, R, 50, Pg, 200, R };
        var loop = new ProgramEntry[ratings.Length];
        for (var i = 0; i < ratings.Length; i++)
        {
            loop[i] = Entry(i, ratings[i], minutes: 60);
        }

        return loop;
    }

    private static ProgramEntry Entry(int index, int? rating, int minutes)
        => new(
            new Guid($"00000000-0000-0000-0000-{index:D12}"),
            "item" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            TimeSpan.FromMinutes(minutes).Ticks,
            "/media/item" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            ParentalRatingValue = rating
        };
}
