using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// A resolved rating constraint: the numeric parental-score bounds and whether unrated content is allowed.
/// A null bound is open on that side ("no floor" / "no cap").
/// </summary>
/// <param name="Min">The lowest parental score allowed, or <c>null</c> for no floor.</param>
/// <param name="Max">The highest parental score allowed, or <c>null</c> for no cap.</param>
/// <param name="IncludeUnrated">Whether items with no rating are allowed.</param>
public readonly record struct RatingWindow(int? Min, int? Max, bool IncludeUnrated)
{
    /// <summary>No restriction: any rating, including unrated.</summary>
    public static readonly RatingWindow Unrestricted = new(null, null, true);

    /// <summary>
    /// Whether a parental score is allowed by this window. A <c>null</c> score means the item is unrated.
    /// </summary>
    /// <param name="parentalScore">The item's parental score, or <c>null</c> when unrated.</param>
    /// <returns><c>true</c> when the item may air under this window.</returns>
    public bool Allows(int? parentalScore)
        => parentalScore is null
            ? IncludeUnrated
            : (Min is null || parentalScore.Value >= Min.Value) && (Max is null || parentalScore.Value <= Max.Value);

    /// <summary>
    /// Combines two windows into the constraint that satisfies the overlap rule: the lowest minimum and the
    /// lowest maximum of the two, with unrated allowed only when both allow it. A null bound counts as the
    /// lowest possible floor / highest possible cap.
    /// </summary>
    /// <param name="other">The window to combine with.</param>
    /// <returns>The combined window.</returns>
    public RatingWindow Combine(RatingWindow other)
        => new(
            Min: Min is null || other.Min is null ? null : Math.Min(Min.Value, other.Min.Value),
            Max: Max is null ? other.Max : other.Max is null ? Max : Math.Min(Max.Value, other.Max.Value),
            IncludeUnrated: IncludeUnrated && other.IncludeUnrated);
}

/// <summary>
/// A <see cref="RatingWindow"/> paired with the time of day it applies, ready for schedule maths (the rating
/// names have already been resolved to numeric scores). All times are minutes since local midnight.
/// </summary>
/// <param name="Window">The rating constraint this block imposes while active.</param>
/// <param name="AllDay">Whether the block applies all day.</param>
/// <param name="StartMinutes">The window start (minutes since local midnight), for a non-all-day block.</param>
/// <param name="EndMinutes">The window end (minutes since local midnight, exclusive), for a non-all-day block.</param>
public readonly record struct ResolvedRatingBlock(RatingWindow Window, bool AllDay, int StartMinutes, int EndMinutes)
{
    /// <summary>
    /// Whether this block is active at the given minute of the day (0-1439). Custom windows are half-open
    /// <c>[start, end)</c> and wrap past midnight when the end is at or before the start.
    /// </summary>
    /// <param name="minuteOfDay">The minute of the day, 0-1439.</param>
    /// <returns><c>true</c> when the block applies at that minute.</returns>
    public bool ActiveAt(int minuteOfDay)
    {
        if (AllDay)
        {
            return true;
        }

        if (StartMinutes == EndMinutes)
        {
            return false; // A zero-length custom window is inert; the configuration validator flags it.
        }

        return StartMinutes < EndMinutes
            ? minuteOfDay >= StartMinutes && minuteOfDay < EndMinutes
            : minuteOfDay >= StartMinutes || minuteOfDay < EndMinutes;
    }
}

/// <summary>
/// Turns a channel's time-of-day rating blocks into the effective rating constraint at any moment. Pure and
/// deterministic so the guide and the live stream independently agree. All times are minutes since local
/// midnight.
/// </summary>
public static class RatingSchedule
{
    /// <summary>The number of minutes in a day.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>
    /// The effective rating window at a minute of the day: the combined constraint of every block active then,
    /// or <see cref="RatingWindow.Unrestricted"/> when none is active.
    /// </summary>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="minuteOfDay">The minute of the day (wrapped into 0-1439).</param>
    /// <returns>The effective window at that minute.</returns>
    public static RatingWindow EffectiveWindow(IReadOnlyList<ResolvedRatingBlock> blocks, int minuteOfDay)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var minute = Wrap(minuteOfDay);
        RatingWindow? window = null;
        foreach (var block in blocks)
        {
            if (block.ActiveAt(minute))
            {
                window = window is null ? block.Window : window.Value.Combine(block.Window);
            }
        }

        return window ?? RatingWindow.Unrestricted;
    }

    /// <summary>
    /// The window an item <b>starting</b> at <paramref name="startMinuteOfDay"/> must satisfy: the combination
    /// of every effective window across the next <paramref name="transitionMinutes"/> minutes, so an item that
    /// bleeds across a daypart boundary already complies with the window it runs into. With no transition window
    /// this is just the effective window at the start.
    /// </summary>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="startMinuteOfDay">The item's start minute of the day.</param>
    /// <param name="transitionMinutes">The channel's transition window in minutes.</param>
    /// <returns>The window the starting item must satisfy.</returns>
    public static RatingWindow WindowForStart(IReadOnlyList<ResolvedRatingBlock> blocks, int startMinuteOfDay, int transitionMinutes)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var start = Wrap(startMinuteOfDay);
        var window = EffectiveWindow(blocks, start);

        var span = Math.Clamp(transitionMinutes, 0, MinutesPerDay);
        if (span == 0)
        {
            return window;
        }

        // The effective window is piecewise-constant with breakpoints at each custom block's start and end.
        // Sampling just inside every segment the [start, start+span] span enters (i.e. at each breakpoint that
        // falls within it) captures every distinct window the span crosses, so the combine covers them all.
        foreach (var block in blocks)
        {
            if (block.AllDay)
            {
                continue;
            }

            foreach (var boundary in new[] { block.StartMinutes, block.EndMinutes })
            {
                var forward = Wrap(boundary - start);
                if (forward > 0 && forward <= span)
                {
                    window = window.Combine(EffectiveWindow(blocks, boundary));
                }
            }
        }

        return window;
    }

    /// <summary>
    /// Whether a parental score is allowed by the effective window at some point in the day (the union across the
    /// day's windows), so a capped population can be built that still has content for every window. A single
    /// all-day band collapses to that band; no blocks means unrestricted.
    /// </summary>
    /// <param name="blocks">The resolved rating blocks.</param>
    /// <param name="parentalScore">The item's parental score, or <c>null</c> when unrated.</param>
    /// <returns><c>true</c> when some window admits the score.</returns>
    public static bool AllowedByAnyWindow(IReadOnlyList<ResolvedRatingBlock> blocks, int? parentalScore)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            return true;
        }

        // The effective window is piecewise-constant with breakpoints at block edges, so sampling minute 0 and each
        // edge covers every distinct window across the day.
        if (EffectiveWindow(blocks, 0).Allows(parentalScore))
        {
            return true;
        }

        foreach (var block in blocks)
        {
            if (block.AllDay)
            {
                continue;
            }

            if (EffectiveWindow(blocks, block.StartMinutes).Allows(parentalScore)
                || EffectiveWindow(blocks, block.EndMinutes).Allows(parentalScore))
            {
                return true;
            }
        }

        return false;
    }

    private static int Wrap(int minute)
    {
        var m = minute % MinutesPerDay;
        return m < 0 ? m + MinutesPerDay : m;
    }
}
