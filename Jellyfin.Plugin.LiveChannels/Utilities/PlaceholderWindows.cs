using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// A channel's placeholder blocks as concrete instants: each block's local-time window on every day, converted to
/// UTC, so the daypart chain can tell whether a moment falls inside one and when the next one opens. A window that
/// wraps past midnight belongs to the day it starts on; on a daylight-saving day its edges follow the wall clock.
/// </summary>
internal sealed class PlaceholderWindows
{
    private readonly (RatingBlock Block, int Start, int Length)[] _blocks;
    private readonly TimeZoneInfo _timeZone;

    // Each local day's windows in UTC, worked out once: a chain walk asks about every item it places.
    private readonly Dictionary<DateTime, (RatingBlock Block, DateTime Start, DateTime End)[]> _days = new();

    // The last answers, which hold for every instant up to where they change: a walk asks about one instant after
    // another, almost always inside the same stretch.
    private (DateTime From, DateTime Until, DateTime End, RatingBlock? Block) _last;
    private (DateTime From, DateTime Next) _next;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderWindows"/> class.</summary>
    /// <param name="blocks">The channel's placeholder blocks (see <see cref="Channel.PlaceholderBlocks"/>).</param>
    /// <param name="timeZone">The time zone the block times are in (server local).</param>
    public PlaceholderWindows(IEnumerable<RatingBlock> blocks, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(timeZone);
        _timeZone = timeZone;
        _blocks = blocks
            .Select(b => (Block: b, Start: RatingSchedule.NormalizeMinute(b.StartMinutes), Length: RatingSchedule.NormalizeMinute(b.EndMinutes - b.StartMinutes)))
            .Where(b => b.Length > 0)
            .ToArray();
    }

    /// <summary>Gets a value indicating whether there are no placeholder windows at all.</summary>
    public bool IsEmpty => _blocks.Length == 0;

    /// <summary>
    /// The placeholder window covering <paramref name="utc"/>, if any: its block and when it ends. Should two
    /// windows overlap, the one ending last is taken.
    /// </summary>
    /// <param name="utc">The instant.</param>
    /// <param name="endUtc">The window's end.</param>
    /// <param name="block">The window's block.</param>
    /// <returns>Whether a window covers the instant.</returns>
    public bool TryFindAt(DateTime utc, out DateTime endUtc, out RatingBlock block)
    {
        if (_last.Until != default && _last.From <= utc && utc < _last.Until)
        {
            endUtc = _last.End;
            block = _last.Block!;
            return _last.Block is not null;
        }

        endUtc = default;
        block = null!;
        var from = DateTime.MinValue;
        foreach (var (candidate, start, end) in WindowsAround(utc))
        {
            if (start <= utc && utc < end && end > endUtc)
            {
                endUtc = end;
                block = candidate;
                from = start;
            }
        }

        if (endUtc != default)
        {
            _last = (from, endUtc, endUtc, block);
            return true;
        }

        // Outside every window until the next one opens (or, with none ahead, for this instant only).
        _last = NextStartAfter(utc) is { } next ? (utc, next, default, null) : default;
        return false;
    }

    /// <summary>The first instant after <paramref name="utc"/> at which a placeholder window opens, or <c>null</c> when none does within two days.</summary>
    /// <param name="utc">The instant.</param>
    /// <returns>The next window's start, or <c>null</c>.</returns>
    public DateTime? NextStartAfter(DateTime utc)
    {
        if (_next.Next != default && _next.From <= utc && utc < _next.Next)
        {
            return _next.Next;
        }

        DateTime? next = null;
        foreach (var (_, start, _) in WindowsAround(utc))
        {
            if (start > utc && (next is null || start < next))
            {
                next = start;
            }
        }

        if (next is { } found)
        {
            _next = (utc, found);
        }

        return next;
    }

    // Every window starting from the local day before the instant's to two days after it, in UTC.
    private IEnumerable<(RatingBlock Block, DateTime Start, DateTime End)> WindowsAround(DateTime utc)
    {
        if (_blocks.Length == 0)
        {
            return [];
        }

        var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _timeZone).Date;
        return Enumerable.Range(-1, 4).SelectMany(d => WindowsOn(DateTime.SpecifyKind(day.AddDays(d), DateTimeKind.Unspecified)));
    }

    private (RatingBlock Block, DateTime Start, DateTime End)[] WindowsOn(DateTime date)
    {
        if (!_days.TryGetValue(date, out var windows))
        {
            windows = _blocks
                .Select(b => (b.Block, Start: ToUtc(date.AddMinutes(b.Start)), End: ToUtc(date.AddMinutes(b.Start + b.Length))))
                .Where(w => w.End > w.Start)
                .ToArray();
            _days[date] = windows;
        }

        return windows;
    }

    // A local wall-clock time in UTC. A time the clocks skip (spring forward) is taken an hour later, as the wall
    // clock reads after the jump; a time they repeat (fall back) is taken at its first occurrence.
    private DateTime ToUtc(DateTime local)
    {
        if (_timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        if (_timeZone.IsAmbiguousTime(local))
        {
            var offsets = _timeZone.GetAmbiguousTimeOffsets(local);
            var daylight = offsets.Max();
            return DateTime.SpecifyKind(local - daylight, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _timeZone);
    }
}
