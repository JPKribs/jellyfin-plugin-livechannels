using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Builds a time-of-day-aware wall-clock schedule for a channel whose rating limits vary by daypart. Unlike the
/// free-running <see cref="ScheduleCalculator"/> loop, the content that airs depends on the clock, so items are
/// placed back to back in one continuous chain simulated from a fixed anchor: local midnight of the day the
/// plugin configuration was last saved. The chain is a deterministic function of the loop, the blocks, and the
/// anchor, so the guide and the live stream independently agree. Each item is placed under the effective rating
/// window for its start time (widened by the channel's transition buffer) and skipped when it does not comply.
/// Nothing is ever truncated: an item that starts before midnight simply runs across it, and the next pick
/// happens under whatever window is active when it ends.
/// </summary>
public static class DaypartSchedule
{
    /// <summary>A hard cap on emitted programmes so a channel of very short items cannot produce an unbounded schedule.</summary>
    private const int MaxPrograms = 10000;

    /// <summary>A hard cap on chain steps per build, bounding a pathologically old anchor. Every configuration
    /// save re-anchors the chain, so real walks cover days, not years; at three-minute items this cap still
    /// reaches over five years past the anchor.</summary>
    private const int MaxWalk = 1_000_000;

    /// <summary>
    /// Simulates the chain from the anchor and returns the programmes covering <c>[fromUtc, toUtc)</c>.
    /// </summary>
    /// <param name="loop">The channel's resolved loop (loop-builder ordered), carrying each item's parental score.</param>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="transitionMinutes">The channel's transition buffer, in minutes.</param>
    /// <param name="timeZone">The time zone the block times are expressed in (server local).</param>
    /// <param name="anchorUtc">The chain anchor (the last configuration save); the chain starts at local midnight of its day.</param>
    /// <param name="fromUtc">The inclusive UTC start of the window.</param>
    /// <param name="toUtc">The exclusive UTC end of the window.</param>
    /// <param name="seed">A per-channel seed (the channel id) so different channels lead the chain differently.</param>
    /// <returns>The ordered, contiguous programmes covering the window (the first may start before <paramref name="fromUtc"/>; nothing precedes the anchor).</returns>
    public static IReadOnlyList<ScheduledProgram> Build(
        IReadOnlyList<ProgramEntry> loop,
        IReadOnlyList<ResolvedRatingBlock> blocks,
        int transitionMinutes,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
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

        // The chain starts at local midnight of the anchor's day, so the whole save day is covered.
        var anchorLocal = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZone);
        var anchorDayLocal = DateTime.SpecifyKind(anchorLocal.Date, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(anchorDayLocal))
        {
            anchorDayLocal = anchorDayLocal.AddHours(1); // A zone whose DST jump skips midnight.
        }

        var t = TimeZoneInfo.ConvertTimeToUtc(anchorDayLocal, timeZone);
        var cursor = SeededStart(seed, anchorDayLocal, loop.Count);

        for (var walked = 0; walked < MaxWalk && t < toUtc && schedule.Count < MaxPrograms; walked++)
        {
            var localT = TimeZoneInfo.ConvertTimeFromUtc(t, timeZone);
            var minute = (localT.Hour * 60) + localT.Minute;
            var window = RatingSchedule.WindowForStart(blocks, minute, transitionMinutes);

            cursor = PickNext(loop, cursor, window, out var item);
            var stop = t + TimeSpan.FromTicks(item.DurationTicks);
            if (stop <= t)
            {
                break; // Defensive: a non-positive duration would not advance the clock.
            }

            if (stop > fromUtc)
            {
                schedule.Add(new ScheduledProgram(item, t, stop));
            }

            t = stop;
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

    // A stable start index into the loop for the chain's first pick, so different channels (and different anchor
    // days) lead with different content while the guide and stream still agree. FNV-1a of the channel seed and
    // the anchor's local date.
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
