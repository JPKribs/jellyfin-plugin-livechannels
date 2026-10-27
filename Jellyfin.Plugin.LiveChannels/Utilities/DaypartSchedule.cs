using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;

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
/// <param name="Pending">How many items of an already-accepted block are still to air from the cursor, so a resumed
/// walk finishes the block exactly as a fresh walk would instead of re-checking its tail as a new block.</param>
internal readonly record struct DaypartChainState(DateTime AnchorUtc, DateTime TimeUtc, int Cursor, int Pending = 0);

/// <summary>
/// Builds a time-of-day-aware wall-clock schedule for a channel whose rating limits vary by daypart. Unlike the
/// free-running <see cref="ScheduleCalculator"/> loop, the content that airs depends on the clock, so items are
/// placed back to back in one continuous chain simulated from a fixed anchor: local midnight of the day the
/// plugin configuration was last saved. The chain is a deterministic function of the loop, the blocks, and the
/// anchor, so the guide and the live stream independently agree. Each item is placed under the effective rating
/// window for its start time (widened by the channel's transition buffer) and skipped when it does not comply.
/// A series block (the loop's consecutive episodes of one series) is placed whole or not at all: it is checked
/// against the window covering its entire airtime, so a daypart border never cuts a batch down to one episode.
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
    /// <param name="maxBlock">The longest run of one series' consecutive episodes treated as a single block (the
    /// channel's episodes per block for an ordered loop, whose series run straight through; unbounded for a shuffled
    /// loop, where each run already is one block).</param>
    /// <param name="keepMultiPart">When <c>true</c> a block reaching <paramref name="maxBlock"/> still extends over the
    /// rest of a multi-part story ("(1)", "(2)", "(3)"...), so the story is never split across blocks.</param>
    /// <returns>The ordered, contiguous programmes covering the window (the first may start before <paramref name="fromUtc"/>; nothing precedes the anchor).</returns>
    public static IReadOnlyList<ScheduledProgram> Build(
        IReadOnlyList<ProgramEntry> loop,
        IReadOnlyList<ResolvedRatingBlock> blocks,
        int transitionMinutes,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime fromUtc,
        DateTime toUtc,
        string seed,
        int maxBlock = int.MaxValue,
        bool keepMultiPart = false)
        => BuildResumable(loop, blocks, transitionMinutes, timeZone, anchorUtc, fromUtc, toUtc, seed, resume: null, out _, maxBlock, keepMultiPart: keepMultiPart);

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
    /// <param name="maxBlock">The longest run of one series' consecutive episodes treated as a single block.</param>
    /// <param name="keepMultiPart">When <c>true</c> a block extends past <paramref name="maxBlock"/> to keep a multi-part story whole.</param>
    /// <param name="exactStart">When <c>true</c> the chain starts exactly at <paramref name="anchorUtc"/> with the
    /// loop's first item (a spliced loop, rotated so the item airing when it was rebuilt comes first), instead of at
    /// local midnight of the anchor's day with a seeded lead item.</param>
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
        out DaypartChainState state,
        int maxBlock = int.MaxValue,
        bool exactStart = false,
        bool keepMultiPart = false)
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

        // Every slot opens with the loop's Up Next bumper (none when the channel shows none).
        var bumper = ProgramLoop.LeadOf(loop);

        // A transition buffer past a whole day adds nothing (the span already covers every window) and, unclamped,
        // overflows the block-span sum below into a negative span that silently disables the rating check.
        transitionMinutes = Math.Clamp(transitionMinutes, 0, RatingSchedule.MinutesPerDay);

        DateTime t;
        int cursor;
        var pending = 0;
        var forceFirst = false;
        if (resume is { } prior && prior.AnchorUtc == anchorUtc && prior.TimeUtc != default
            && prior.TimeUtc <= fromUtc && prior.Cursor >= 0 && prior.Cursor < loop.Count)
        {
            // Continue the chain where the previous window's walk stopped, skipping the anchor re-walk.
            t = prior.TimeUtc;
            cursor = prior.Cursor;
            pending = Math.Max(0, prior.Pending);
        }
        else if (exactStart)
        {
            // The first item is the one that was already on air when the loop was spliced: it airs as-is, never
            // re-checked as the head of a new block. Re-checking it at its own start minute (and against a block
            // formed from the rotated loop) could skip it and jump the stream mid-show.
            t = anchorUtc;
            cursor = 0;
            forceFirst = true;
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
            ProgramEntry item;
            if (forceFirst)
            {
                item = loop[cursor];
                cursor = (cursor + 1) % loop.Count;
                forceFirst = false;
            }
            else if (pending > 0)
            {
                // The rest of a block that was checked whole when it started.
                item = loop[cursor];
                cursor = (cursor + 1) % loop.Count;
                pending--;
            }
            else
            {
                var localT = TimeZoneInfo.ConvertTimeFromUtc(t, timeZone);
                var minute = (localT.Hour * 60) + localT.Minute;
                if (PickBlock(loop, cursor, blocks, minute, transitionMinutes, maxBlock, keepMultiPart, out var start, out var length))
                {
                    item = loop[start];
                    cursor = (start + 1) % loop.Count;
                    pending = length - 1;
                }
                else
                {
                    // No whole block fits anywhere in the loop: fall back to the single item that does (or the
                    // lowest-rated one), so the channel is never dead air.
                    cursor = PickNext(loop, cursor, RatingSchedule.WindowForStart(blocks, minute, transitionMinutes), out item);
                }
            }

            var stop = t + TimeSpan.FromTicks(item.DurationTicks + bumper);
            if (stop <= t)
            {
                break; // Defensive: a non-positive duration would not advance the clock.
            }

            if (stop > fromUtc)
            {
                schedule.Add(new ScheduledProgram(item, t, stop) { Lead = TimeSpan.FromTicks(bumper) });
            }

            t = stop;
        }

        state = new DaypartChainState(anchorUtc, t, cursor, pending);
        return schedule;
    }

    // The first block at or after the cursor (wrapping once) that airs whole under the rating window covering its
    // entire airtime: from its start until its last item's start plus the transition buffer, the same span each of
    // its items would be checked against on its own, so placing the block whole is never less strict than placing
    // it item by item. A block is a run of one series' consecutive episodes (at most maxBlock long, plus the rest of a multi-part
    // story it ends inside); a movie or
    // other standalone item is a block of one. A block that does not fit is skipped whole.
    private static bool PickBlock(IReadOnlyList<ProgramEntry> loop, int cursor, IReadOnlyList<ResolvedRatingBlock> blocks, int minute, int transitionMinutes, int maxBlock, bool keepMultiPart, out int start, out int length)
    {
        var scanned = 0;
        while (scanned < loop.Count)
        {
            start = (cursor + scanned) % loop.Count;
            length = BlockLength(loop, start, Math.Max(1, maxBlock), loop.Count - scanned, keepMultiPart);

            long leadTicks = 0;
            for (var k = 0; k < length - 1; k++)
            {
                leadTicks += ProgramLoop.SlotTicks(loop, (start + k) % loop.Count);
            }

            var span = transitionMinutes + (int)Math.Min(RatingSchedule.MinutesPerDay, Math.Ceiling(TimeSpan.FromTicks(leadTicks).TotalMinutes));
            var window = RatingSchedule.WindowForStart(blocks, minute, span);
            var fits = true;
            for (var k = 0; k < length && fits; k++)
            {
                fits = window.Allows(loop[(start + k) % loop.Count].ParentalRatingValue);
            }

            if (fits)
            {
                return true;
            }

            scanned += length;
        }

        start = cursor;
        length = 0;
        return false;
    }

    // How many consecutive items from start belong to the same series (capped), or 1 for a standalone item. With
    // keepMultiPart a block that reaches the cap still runs on through the rest of a multi-part story, so (1)...(4)
    // air together; limit (the items left to scan) is never exceeded.
    private static int BlockLength(IReadOnlyList<ProgramEntry> loop, int start, int cap, int limit, bool keepMultiPart)
    {
        var series = loop[start].SeriesId;
        if (series is null)
        {
            return 1;
        }

        cap = Math.Min(cap, limit);
        var length = 1;
        while (length < cap && loop[(start + length) % loop.Count].SeriesId == series)
        {
            length++;
        }

        while (keepMultiPart && length < limit
            && ProgramLoopBuilder.ContinuesPart(loop[(start + length - 1) % loop.Count], loop[(start + length) % loop.Count]))
        {
            length++;
        }

        return length;
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
