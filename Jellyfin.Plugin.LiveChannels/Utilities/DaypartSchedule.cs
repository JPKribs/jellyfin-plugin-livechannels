using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Builds a time-of-day-aware wall-clock schedule for a channel whose rating limits vary by daypart. Unlike the
/// free-running <see cref="ScheduleCalculator"/> loop, the content that airs depends on the clock, so the schedule
/// is simulated forward from local midnight -- a deterministic function of the loop, the blocks, and the date, so
/// the guide and the live stream independently agree. Each item is placed under the effective rating window for its
/// start time (widened by the channel's transition buffer) and skipped when it does not fit, so an item airs only
/// while it is allowed. Items are truncated at local midnight, the daily anchor that keeps the simulation bounded.
/// </summary>
public static class DaypartSchedule
{
    /// <summary>A hard cap on placed programmes so a channel of very short items cannot produce an unbounded schedule.</summary>
    private const int MaxPrograms = 10000;

    /// <summary>
    /// Simulates the schedule covering <c>[fromUtc, toUtc)</c>.
    /// </summary>
    /// <param name="loop">The channel's resolved loop (loop-builder ordered), carrying each item's parental score.</param>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="transitionMinutes">The channel's transition buffer, in minutes.</param>
    /// <param name="timeZone">The time zone the block times are expressed in (server local).</param>
    /// <param name="fromUtc">The inclusive UTC start of the window.</param>
    /// <param name="toUtc">The exclusive UTC end of the window.</param>
    /// <param name="seed">A per-channel seed (the channel id) so different channels lead each day differently.</param>
    /// <returns>The ordered, contiguous programmes covering the window (the first may start before <paramref name="fromUtc"/>).</returns>
    public static IReadOnlyList<ScheduledProgram> Build(
        IReadOnlyList<ProgramEntry> loop,
        IReadOnlyList<ResolvedRatingBlock> blocks,
        int transitionMinutes,
        TimeZoneInfo timeZone,
        DateTime fromUtc,
        DateTime toUtc,
        string seed)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentException.ThrowIfNullOrEmpty(seed);

        var schedule = new List<ScheduledProgram>();
        if (loop.Count == 0 || toUtc <= fromUtc)
        {
            return schedule;
        }

        // Start at local midnight of the day fromUtc falls in, so an item already airing at fromUtc is captured.
        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZone);
        var dayLocal = DateTime.SpecifyKind(localFrom.Date, DateTimeKind.Unspecified);

        while (schedule.Count < MaxPrograms)
        {
            var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(dayLocal, timeZone);
            var nextMidnightLocal = DateTime.SpecifyKind(dayLocal.AddDays(1), DateTimeKind.Unspecified);
            var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightLocal, timeZone);

            if (dayStartUtc >= toUtc)
            {
                break;
            }

            var cursor = SeededStart(seed, dayLocal, loop.Count);
            var t = dayStartUtc;
            while (t < nextMidnightUtc && schedule.Count < MaxPrograms)
            {
                var localT = TimeZoneInfo.ConvertTimeFromUtc(t, timeZone);
                var minute = (localT.Hour * 60) + localT.Minute;
                var window = RatingSchedule.WindowForStart(blocks, minute, transitionMinutes);

                cursor = PickNext(loop, cursor, window, out var item);
                var stop = t + TimeSpan.FromTicks(item.DurationTicks);
                if (stop > nextMidnightUtc)
                {
                    stop = nextMidnightUtc; // Truncate at midnight -- the daily anchor that bounds the simulation.
                }

                if (stop <= t)
                {
                    break; // Defensive: a non-positive duration would not advance the clock.
                }

                if (stop > fromUtc && t < toUtc)
                {
                    schedule.Add(new ScheduledProgram(item, t, stop));
                }

                t = stop;
            }

            dayLocal = nextMidnightLocal;
        }

        return schedule;
    }

    // The next item at or after the cursor that the window allows, wrapping once, and the cursor just past it. When
    // nothing fits (a window with no matching content -- a misconfiguration) the lowest-rated item airs, so the
    // channel is never dead air.
    private static int PickNext(IReadOnlyList<ProgramEntry> loop, int cursor, RatingWindow window, out ProgramEntry item)
    {
        for (var scanned = 0; scanned < loop.Count; scanned++)
        {
            var index = (cursor + scanned) % loop.Count;
            if (window.Allows(loop[index].ParentalRatingValue))
            {
                item = loop[index];
                return (index + 1) % loop.Count;
            }
        }

        var lowest = 0;
        for (var i = 1; i < loop.Count; i++)
        {
            if (Score(loop[i]) < Score(loop[lowest]))
            {
                lowest = i;
            }
        }

        item = loop[lowest];
        return (lowest + 1) % loop.Count;
    }

    private static int Score(ProgramEntry entry) => entry.ParentalRatingValue ?? int.MaxValue;

    // A stable per-day start index into the loop, so the day leads with different content over time while the guide
    // and stream still agree for any given date. FNV-1a of the channel seed and the local date.
    private static int SeededStart(string seed, DateTime dayLocal, int count)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in seed)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            foreach (var c in dayLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            return (int)(hash % (uint)count);
        }
    }
}
