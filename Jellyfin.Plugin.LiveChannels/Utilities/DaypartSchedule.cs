using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// The daypart chain's simulation state at a moment: the next pick happens at <paramref name="TimeUtc"/> with the
/// loop cursor at <paramref name="Cursor"/>. Capturing it after one build and resuming the next from it skips
/// re-walking the whole chain from the anchor (which grows without bound over a long session). Valid only for the
/// same loop, blocks, and anchor; <paramref name="AnchorUtc"/> is carried so a resume against a different anchor
/// (a configuration save) is detected and ignored.
/// </summary>
/// <param name="AnchorUtc">The anchor the chain was simulated from, keying the state's validity.</param>
/// <param name="TimeUtc">Where the walk stopped: the UTC instant the next pick happens at.</param>
/// <param name="Cursor">The loop cursor for that next pick.</param>
internal readonly record struct DaypartChainState(DateTime AnchorUtc, DateTime TimeUtc, int Cursor);

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
        => BuildResumable(loop, blocks, transitionMinutes, timeZone, anchorUtc, fromUtc, toUtc, seed, resume: null, out _);

    /// <summary>
    /// The same simulation, resumable: when <paramref name="resume"/> is a state captured from a previous call
    /// with the same loop, blocks, and anchor, and it does not lie past <paramref name="fromUtc"/>, the walk
    /// continues from it instead of re-simulating the whole chain from the anchor. The state where this walk
    /// stopped is returned in <paramref name="state"/> for the next call. The chain is deterministic, so a
    /// resumed walk emits exactly what a fresh walk over the same window would.
    /// </summary>
    /// <param name="loop">The channel's resolved loop (loop-builder ordered), carrying each item's parental score.</param>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="transitionMinutes">The channel's transition buffer, in minutes.</param>
    /// <param name="timeZone">The time zone the block times are expressed in (server local).</param>
    /// <param name="anchorUtc">The chain anchor (the last configuration save); the chain starts at local midnight of its day.</param>
    /// <param name="fromUtc">The inclusive UTC start of the window.</param>
    /// <param name="toUtc">The exclusive UTC end of the window.</param>
    /// <param name="seed">A per-channel seed (the channel id) so different channels lead the chain differently.</param>
    /// <param name="resume">A state from a previous call to continue from, or <c>null</c> to walk from the anchor. Ignored (with a fresh anchor walk) when it belongs to a different anchor, lies past the window start, or no longer fits the loop.</param>
    /// <param name="state">The walk's exit state, valid to resume the next window from.</param>
    /// <returns>The ordered, contiguous programmes covering the window (the first may start before <paramref name="fromUtc"/>; nothing precedes the anchor).</returns>
    internal static IReadOnlyList<ScheduledProgram> BuildResumable(
        IReadOnlyList<ProgramEntry> loop,
        IReadOnlyList<ResolvedRatingBlock> blocks,
        int transitionMinutes,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime fromUtc,
        DateTime toUtc,
        string seed,
        DaypartChainState? resume,
        out DaypartChainState state)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentException.ThrowIfNullOrEmpty(seed);

        var schedule = new List<ScheduledProgram>();
        if (loop.Count == 0 || toUtc <= fromUtc)
        {
            // Nothing was walked; hand back whatever state the caller had (or an inert default).
            state = resume ?? default;
            return schedule;
        }

        DateTime t;
        int cursor;
        if (resume is { } prior && prior.AnchorUtc == anchorUtc && prior.TimeUtc != default
            && prior.TimeUtc <= fromUtc && prior.Cursor >= 0 && prior.Cursor < loop.Count)
        {
            // Continue the chain where the previous window's walk stopped, skipping the anchor re-walk.
            t = prior.TimeUtc;
            cursor = prior.Cursor;
        }
        else
        {
            // The chain starts at local midnight of the anchor's day, so the whole save day is covered.
            var anchorLocal = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZone);
            var anchorDayLocal = DateTime.SpecifyKind(anchorLocal.Date, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(anchorDayLocal))
            {
                anchorDayLocal = anchorDayLocal.AddHours(1); // A zone whose DST jump skips midnight.
            }

            t = TimeZoneInfo.ConvertTimeToUtc(anchorDayLocal, timeZone);
            cursor = SeededStart(seed, anchorDayLocal, loop.Count);
        }

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

        state = new DaypartChainState(anchorUtc, t, cursor);
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
