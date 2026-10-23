using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests that series batches air whole (a shuffled tail block is topped up, a daypart border skips a block rather
/// than cutting it), and that a spliced loop keeps the program on air across a rebuild.
/// </summary>
public class ScheduleContinuityTests
{
    private const int Pg = 100;
    private const int R = 400;
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;
    private static readonly DateTime Midnight = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProgramEntry Ep(Guid series, int number, int? rating = null, long? ticks = null)
        => new(Guid.NewGuid(), "Show " + number, null, ticks ?? Hour, "/e.mkv")
        {
            SeriesId = series,
            SeriesName = "Show",
            SeasonNumber = 1,
            EpisodeNumber = number,
            RawName = "e" + number,
            ParentalRatingValue = rating
        };

    private static ProgramEntry Movie(string title, int? rating = null)
        => new(Guid.NewGuid(), title, null, Hour, "/m.mkv") { IsMovie = true, ParentalRatingValue = rating };

    [Fact]
    public void Shuffle_EveryRotationAirsAFullBatch()
    {
        // Nine episodes at four per block leave a one-episode tail; whichever block the rotation lands on, the
        // series must still air four episodes.
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 9).Select(i => Ep(s, i)).ToList();

        for (var rotation = 0; rotation < 6; rotation++)
        {
            var loop = ProgramLoopBuilder.Build(items, new ChannelLoopOptions(4, true, LoopMode.Shuffle, false, "ch1", Rotation: rotation));
            Assert.Equal(4, loop.Count(e => e.SeriesId == s));
            Assert.Equal(4, loop.Where(e => e.SeriesId == s).Select(e => e.ItemId).Distinct().Count());
        }
    }

    [Fact]
    public void Shuffle_SeriesShorterThanABlockIsNotPadded()
    {
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 3).Select(i => Ep(s, i)).ToList();

        var loop = ProgramLoopBuilder.Build(items, new ChannelLoopOptions(4, true, LoopMode.Shuffle, false, "ch1"));
        Assert.Equal(3, loop.Count);
    }

    [Fact]
    public void Daypart_BorderSkipsAWholeBlockInsteadOfCuttingIt()
    {
        // 00:00-02:00 allows R, from 02:00 only PG. A four-hour R-rated batch starting at 00:00 would cross the
        // border, so it must be skipped whole: before the fix its first two episodes aired and the batch was cut.
        var blocks = new[]
        {
            new ResolvedRatingBlock(new RatingWindow(null, R, true), AllDay: false, 0, 2 * 60),
            new ResolvedRatingBlock(new RatingWindow(null, Pg, true), AllDay: false, 2 * 60, 0)
        };
        var adult = Guid.NewGuid();
        var kids = Guid.NewGuid();
        var loop = new List<ProgramEntry>();
        loop.AddRange(Enumerable.Range(1, 4).Select(i => Ep(adult, i, R)));
        loop.AddRange(Enumerable.Range(1, 4).Select(i => Ep(kids, i, Pg)));
        loop.Add(Movie("Late", R));

        var schedule = DaypartSchedule.Build(loop, blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(12), "chan");

        foreach (var run in Runs(schedule))
        {
            Assert.True(run.SeriesId is null || run.Count == 4, $"series batch aired {run.Count} episode(s) instead of 4");
        }

        Assert.DoesNotContain(schedule, slot => slot.Program.SeriesId == adult);
    }

    [Fact]
    public void Daypart_ResumedWalkMatchesFreshMidBlock()
    {
        var blocks = new[] { new ResolvedRatingBlock(new RatingWindow(null, R, true), AllDay: false, 0, 12 * 60) };
        var s = Guid.NewGuid();
        var loop = Enumerable.Range(1, 4).Select(i => Ep(s, i, Pg)).Append(Movie("M", Pg)).ToList();

        // The first window ends two episodes into a four-episode block.
        DaypartSchedule.BuildResumable(loop, blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(2), "chan", null, out var state);
        var resumed = DaypartSchedule.BuildResumable(loop, blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight.AddHours(2), Midnight.AddHours(10), "chan", state, out _);
        var fresh = DaypartSchedule.Build(loop, blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight.AddHours(2), Midnight.AddHours(10), "chan");

        Assert.Equal(fresh.Select(p => (p.Program.ItemId, p.Start)), resumed.Select(p => (p.Program.ItemId, p.Start)));
    }

    [Fact]
    public void SplicedLoop_KeepsTheAiringProgramInItsSlot()
    {
        // A rebuilt loop rotated to lead with the airing item and anchored at its original start projects the
        // airing program exactly where the old schedule had it, so the guide keeps the same program id.
        var old = Enumerable.Range(0, 5).Select(i => Movie("m" + i)).ToList();
        var now = ScheduleCalculator.Epoch.AddHours(1000).AddMinutes(25);
        var airing = ScheduleCalculator.BuildSchedule(old, now, now.AddTicks(1), ScheduleCalculator.Epoch)[0];

        var rebuilt = new List<ProgramEntry> { Movie("new"), airing.Program, Movie("other") };
        var spliced = new ProgramLoop(rebuilt.Skip(1).Concat(rebuilt.Take(1)).ToList(), airing.Start);
        var after = ScheduleCalculator.BuildSchedule(spliced, now, now.AddHours(3), ScheduleCalculator.AnchorFor(spliced));

        Assert.Equal(airing.Program.ItemId, after[0].Program.ItemId);
        Assert.Equal(airing.Start, after[0].Start);
        Assert.Equal(airing.Stop, after[0].Stop);
    }

    [Fact]
    public void SplicedDaypart_StartsExactlyAtTheAnchorWithTheFirstItem()
    {
        var blocks = new[] { new ResolvedRatingBlock(new RatingWindow(null, R, true), AllDay: false, 0, 12 * 60) };
        var loop = Enumerable.Range(0, 4).Select(i => Movie("m" + i, Pg)).ToList();
        var anchor = Midnight.AddHours(3).AddMinutes(17);

        var schedule = DaypartSchedule.BuildResumable(loop, blocks, 0, TimeZoneInfo.Utc, anchor, anchor.AddMinutes(30), anchor.AddHours(4), "chan", null, out _, exactStart: true);

        Assert.Equal(loop[0].ItemId, schedule[0].Program.ItemId);
        Assert.Equal(anchor, schedule[0].Start);
    }

    // Consecutive runs of the schedule's programs by series (standalone items are runs of one).
    private static List<(Guid? SeriesId, int Count)> Runs(IReadOnlyList<ScheduledProgram> schedule)
    {
        var runs = new List<(Guid? SeriesId, int Count)>();
        foreach (var slot in schedule)
        {
            var series = slot.Program.SeriesId;
            if (runs.Count > 0 && series is not null && runs[^1].SeriesId == series && runs[^1].Count < 4)
            {
                runs[^1] = (series, runs[^1].Count + 1);
            }
            else
            {
                runs.Add((series, 1));
            }
        }

        // The window may cut the final run short; it is not a scheduling cut.
        if (runs.Count > 0 && runs[^1].SeriesId is not null)
        {
            runs.RemoveAt(runs.Count - 1);
        }

        return runs;
    }
}
