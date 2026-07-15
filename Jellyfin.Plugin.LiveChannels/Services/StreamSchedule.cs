using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// One item for the per-item stream loop to play: the program, how far into it to start, and an optional playback
/// cap (used to end an item at its scheduled stop when the time-of-day schedule truncated it at midnight).
/// </summary>
/// <param name="Program">The item to play.</param>
/// <param name="Offset">How far into the item to start (non-zero only on the tune-in item).</param>
/// <param name="DurationLimit">A playback cap from the seek point, or <c>null</c> to play to the natural end.</param>
internal readonly record struct StreamStep(ProgramEntry Program, TimeSpan Offset, TimeSpan? DurationLimit);

/// <summary>
/// The source of the next item for the per-item stream loop: a free-running loop for ordinary channels, or a
/// time-of-day daypart schedule for channels with custom rating blocks.
/// </summary>
internal interface IStreamSchedule
{
    /// <summary>The size of the underlying content pool, used to bound the consecutive-failure cap.</summary>
    int PoolCount { get; }

    /// <summary>The item to play now.</summary>
    StreamStep Current { get; }

    /// <summary>Advances to the next item.</summary>
    void Advance();
}

/// <summary>
/// The classic free-running loop: play from a start index and offset, then each subsequent item in order, wrapping.
/// </summary>
internal sealed class LoopStreamSchedule : IStreamSchedule
{
    private readonly IReadOnlyList<ProgramEntry> _programs;
    private int _index;
    private TimeSpan _offset;

    public LoopStreamSchedule(IReadOnlyList<ProgramEntry> programs, int startIndex, TimeSpan startOffset)
    {
        _programs = programs;
        _index = startIndex;
        _offset = startOffset;
    }

    public int PoolCount => _programs.Count;

    public StreamStep Current => new(_programs[_index], _offset, null);

    public void Advance()
    {
        _offset = TimeSpan.Zero;
        _index = (_index + 1) % _programs.Count;
    }
}

/// <summary>
/// A time-of-day schedule: items are drawn from the channel's daypart timeline (rebuilt in horizon-sized windows),
/// each capped at its scheduled stop so a midnight-truncated item ends where the schedule says it does. The
/// timeline is a deterministic function of the clock, so this plays exactly what the guide shows.
/// </summary>
internal sealed class DaypartStreamSchedule : IStreamSchedule
{
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(6);

    private readonly ChannelService _channels;
    private readonly Channel _channel;
    private readonly IReadOnlyList<ProgramEntry> _pool;
    private List<ScheduledProgram> _window = new();
    private int _pos;
    private bool _first = true;

    public DaypartStreamSchedule(ChannelService channels, Channel channel, IReadOnlyList<ProgramEntry> pool)
    {
        _channels = channels;
        _channel = channel;
        _pool = pool;
        Refill(DateTime.UtcNow);
    }

    public int PoolCount => _pool.Count;

    public StreamStep Current
    {
        get
        {
            var slot = _window[_pos];

            // The tune-in item starts partway through; every later item starts at its own boundary.
            var offset = _first ? DateTime.UtcNow - slot.Start : TimeSpan.Zero;
            if (offset < TimeSpan.Zero)
            {
                offset = TimeSpan.Zero;
            }

            // Cap playback only when the schedule shortened this slot (a midnight truncation), so full-length items
            // still play to their natural end and can have their real duration observed.
            var scheduled = slot.Stop - slot.Start;
            var full = TimeSpan.FromTicks(slot.Program.DurationTicks);
            TimeSpan? limit = scheduled < full - TimeSpan.FromSeconds(1) ? scheduled - offset : null;
            if (limit is { } value && value <= TimeSpan.Zero)
            {
                limit = null;
            }

            return new StreamStep(slot.Program, offset, limit);
        }
    }

    public void Advance()
    {
        _first = false;
        var end = _window[_pos].Stop;
        _pos++;
        if (_pos >= _window.Count)
        {
            Refill(end);
        }
    }

    private void Refill(DateTime fromUtc)
    {
        _window = _channels.BuildTimeline(_channel, _pool, fromUtc, fromUtc + Horizon).ToList();
        if (_window.Count == 0)
        {
            // Degenerate (a non-empty pool always yields at least one slot): fall back to the raw pool, untruncated.
            _window = _pool.Select(p => new ScheduledProgram(p, fromUtc, fromUtc + TimeSpan.FromTicks(p.DurationTicks))).ToList();
        }

        _pos = 0;
    }
}
