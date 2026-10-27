using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// One slot for the per-item stream loop to play: the program, how far into its slot to start, an optional playback
/// cap (a defensive bound used only if the schedule ever hands over a slot shorter than the item), and the Up Next
/// bumper that opens the slot. The offset is measured from the slot's start, so an offset below the lead falls in
/// the bumper and the item itself starts at the lead.
/// </summary>
/// <param name="Program">The item to play.</param>
/// <param name="Offset">How far into the slot to start (non-zero only on the tune-in slot).</param>
/// <param name="DurationLimit">A playback cap from the seek point, or <c>null</c> to play to the natural end.</param>
/// <param name="Lead">The bumper that opens the slot, or zero.</param>
internal readonly record struct StreamStep(ProgramEntry Program, TimeSpan Offset, TimeSpan? DurationLimit, TimeSpan Lead = default);

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

    /// <summary>
    /// The item <paramref name="ahead"/> slots after the current one and the guide time its slot starts, as the
    /// schedule stands now (a rebuild may still change it), or <c>null</c> when the schedule does not reach that far.
    /// </summary>
    /// <param name="ahead">How many slots ahead (1 is the next).</param>
    /// <returns>The item and its slot's start, or <c>null</c>.</returns>
    (ProgramEntry Program, DateTime StartUtc)? Following(int ahead);
}

/// <summary>
/// Notices when a channel's program loop has been rebuilt (the daily guide refresh, or a configuration save)
/// while a session is streaming it, so a viewer who stays tuned follows the same schedule the guide now shows.
/// The re-resolve runs in the background and is only ever waited on briefly: the in-memory hot copy answers
/// instantly, and a slow disk read or rebuild must never stall the stream at an item boundary.
/// </summary>
internal sealed class ProgramsFollower
{
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(30);

    private readonly Func<(Channel Channel, IReadOnlyList<ProgramEntry> Programs)?> _resolve;
    private readonly object _gate = new();
    private Task<(Channel Channel, IReadOnlyList<ProgramEntry> Programs)?>? _pending;
    private long _nextPollStamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramsFollower"/> class.
    /// </summary>
    /// <param name="channel">The channel the session started with.</param>
    /// <param name="programs">The loop the session started with.</param>
    /// <param name="resolve">Resolves the channel's current configuration and loop, or <c>null</c> when the channel is gone.</param>
    public ProgramsFollower(Channel channel, IReadOnlyList<ProgramEntry> programs, Func<(Channel Channel, IReadOnlyList<ProgramEntry> Programs)?> resolve)
    {
        Channel = channel;
        Programs = programs;
        _resolve = resolve;
    }

    /// <summary>Gets the channel configuration the current loop was resolved for.</summary>
    public Channel Channel { get; private set; }

    /// <summary>Gets the loop the session is currently following.</summary>
    public IReadOnlyList<ProgramEntry> Programs { get; private set; }

    /// <summary>
    /// Returns the rebuilt loop when one has appeared since the last call (and adopts it), otherwise <c>null</c>.
    /// Starts a background re-resolve when none is in flight (at most every 30 seconds) and waits up to
    /// <paramref name="wait"/> for it, which the hot copy easily meets.
    /// </summary>
    /// <param name="wait">How long to wait for an in-flight re-resolve.</param>
    /// <returns>The new loop, or <c>null</c> when it is unchanged (or not resolved yet).</returns>
    public IReadOnlyList<ProgramEntry>? TryTakeNewer(TimeSpan wait)
    {
        Task<(Channel Channel, IReadOnlyList<ProgramEntry> Programs)?> task;
        lock (_gate)
        {
            if (_pending is null)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < _nextPollStamp)
                {
                    return null;
                }

                _nextPollStamp = now + (long)(MinPollInterval.TotalSeconds * Stopwatch.Frequency);
                _pending = Task.Run(_resolve);
            }

            task = _pending;
        }

        try
        {
            if (wait > TimeSpan.Zero && !task.IsCompleted)
            {
                task.Wait(wait);
            }
        }
        catch (AggregateException)
        {
            // Surfaced below as a faulted task.
        }

        if (!task.IsCompleted)
        {
            return null;
        }

        lock (_gate)
        {
            _pending = null;
            if (!task.IsCompletedSuccessfully || task.Result is not { } fresh || fresh.Programs.Count == 0 || ReferenceEquals(fresh.Programs, Programs))
            {
                return null;
            }

            Channel = fresh.Channel;
            Programs = fresh.Programs;
            return fresh.Programs;
        }
    }
}

/// <summary>
/// The classic free-running loop: play from a start index and offset, then each subsequent item in order, wrapping.
/// With a <see cref="ProgramsFollower"/>, it tracks the guide time of every boundary, and when the loop is rebuilt
/// mid-session it re-anchors at the next boundary to whatever the rebuilt loop airs then.
/// </summary>
internal sealed class LoopStreamSchedule : IStreamSchedule
{
    // How long to wait at an item boundary for a background re-resolve of the loop.
    private static readonly TimeSpan RefreshWait = TimeSpan.FromMilliseconds(200);

    private readonly ProgramsFollower? _follower;
    private IReadOnlyList<ProgramEntry> _programs;
    private int _index;
    private TimeSpan _offset;
    private DateTime _itemStartUtc;

    public LoopStreamSchedule(IReadOnlyList<ProgramEntry> programs, int startIndex, TimeSpan startOffset, DateTime? startUtc = null, ProgramsFollower? follower = null)
    {
        _programs = programs;
        _index = startIndex;
        _offset = startOffset;
        _itemStartUtc = (startUtc ?? DateTime.UtcNow) - startOffset;
        _follower = follower;
    }

    public int PoolCount => _programs.Count;

    public StreamStep Current => new(_programs[_index], _offset, null, TimeSpan.FromTicks(ProgramLoop.LeadOf(_programs)));

    public (ProgramEntry Program, DateTime StartUtc)? Following(int ahead)
    {
        var index = _index;
        var start = _itemStartUtc;
        for (var i = 0; i < ahead; i++)
        {
            start += TimeSpan.FromTicks(ProgramLoop.SlotTicks(_programs, index));
            index = (index + 1) % _programs.Count;
        }

        return (_programs[index], start);
    }

    public void Advance()
    {
        // The guide time the slot just played ends at, per the loop it was scheduled from.
        var boundary = _itemStartUtc + TimeSpan.FromTicks(ProgramLoop.SlotTicks(_programs, _index));
        if (_follower?.TryTakeNewer(RefreshWait) is { } fresh)
        {
            _programs = fresh;
            (_index, _offset) = StreamScheduleMath.AtBoundary(fresh, boundary);
            _itemStartUtc = boundary - _offset;
            return;
        }

        _offset = TimeSpan.Zero;
        _index = (_index + 1) % _programs.Count;
        _itemStartUtc = boundary;
    }
}

/// <summary>
/// Shared position math for re-anchoring a running session onto a rebuilt loop.
/// </summary>
internal static class StreamScheduleMath
{
    // Seek offsets this close to an item's start (or end) are schedule rounding, not a real mid-item position:
    // play from the start (or move on to the next item) instead of seeking a fraction of a second.
    private static readonly TimeSpan BoundaryTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The item and offset a loop airs at a guide time, snapped to the nearest item boundary when within a couple
    /// of seconds of one.
    /// </summary>
    /// <param name="programs">The loop.</param>
    /// <param name="atUtc">The guide time.</param>
    /// <returns>The item index and how far into it to start.</returns>
    public static (int Index, TimeSpan Offset) AtBoundary(IReadOnlyList<ProgramEntry> programs, DateTime atUtc)
    {
        var (index, offset) = ScheduleCalculator.CurrentProgram(programs, atUtc, ScheduleCalculator.AnchorFor(programs));
        return Snap(programs, index, offset);
    }

    /// <summary>
    /// Snaps an item position to the nearest boundary when within a couple of seconds of one.
    /// </summary>
    /// <param name="programs">The loop.</param>
    /// <param name="index">The item index.</param>
    /// <param name="offset">How far into the item.</param>
    /// <returns>The snapped item index and offset.</returns>
    public static (int Index, TimeSpan Offset) Snap(IReadOnlyList<ProgramEntry> programs, int index, TimeSpan offset)
    {
        if (TimeSpan.FromTicks(ProgramLoop.SlotTicks(programs, index)) - offset < BoundaryTolerance)
        {
            return ((index + 1) % programs.Count, TimeSpan.Zero);
        }

        return (index, offset < BoundaryTolerance ? TimeSpan.Zero : offset);
    }

    /// <summary>Whether a remaining slot length is short enough to treat the slot as already over.</summary>
    /// <param name="remaining">The time left in the slot.</param>
    /// <returns><c>true</c> when the slot should be skipped.</returns>
    public static bool IsSpent(TimeSpan remaining) => remaining < BoundaryTolerance;

    /// <summary>Snaps a near-zero offset to zero.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The snapped offset.</returns>
    public static TimeSpan SnapOffset(TimeSpan offset) => offset < BoundaryTolerance ? TimeSpan.Zero : offset;
}

/// <summary>
/// A time-of-day schedule: items are drawn from the channel's daypart timeline (rebuilt in horizon-sized windows).
/// The timeline is one deterministic chain from the configuration-save anchor, so this plays exactly what the
/// guide shows, and slots are always full length (nothing is truncated at midnight).
/// </summary>
internal sealed class DaypartStreamSchedule : IStreamSchedule
{
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(6);

    // How long to wait at an item boundary for a background re-resolve of the loop.
    private static readonly TimeSpan RefreshWait = TimeSpan.FromMilliseconds(200);

    private readonly ChannelService _channels;
    private readonly ProgramsFollower? _follower;
    private Channel _channel;
    private IReadOnlyList<ProgramEntry> _pool;
    private List<ScheduledProgram> _window = new();
    private int _pos;

    // The guide time playback joins the current slot at: the tune-in moment (advanced by the tune-in card) for the
    // first item, or the boundary a rebuilt loop was re-anchored at. Null once playing slots from their starts.
    private DateTime? _joinUtc;

    // Where the last refill's chain walk stopped, so the next refill resumes there instead of re-simulating
    // from the anchor (each successive re-walk of a long-running session would otherwise get longer).
    private DaypartChainState? _chain;

    /// <summary>
    /// Initializes a new instance of the <see cref="DaypartStreamSchedule"/> class.
    /// </summary>
    /// <param name="channels">The channel service that builds the daypart timeline.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="pool">The channel's program loop.</param>
    /// <param name="startUtc">The guide time to join at: now, advanced by any tune-in card so content lands on the wall clock once the card ends.</param>
    /// <param name="follower">Watches for a rebuilt loop mid-session, or <c>null</c>.</param>
    public DaypartStreamSchedule(ChannelService channels, Channel channel, IReadOnlyList<ProgramEntry> pool, DateTime? startUtc = null, ProgramsFollower? follower = null)
    {
        _channels = channels;
        _channel = channel;
        _pool = pool;
        _follower = follower;
        var join = startUtc ?? DateTime.UtcNow;
        Refill(join);
        JoinAt(join);
    }

    public int PoolCount => _pool.Count;

    public StreamStep Current
    {
        get
        {
            var slot = _window[_pos];

            // The joined item starts partway through; every later item starts at its own boundary.
            var offset = _joinUtc is { } join ? StreamScheduleMath.SnapOffset(join - slot.Start) : TimeSpan.Zero;
            if (offset < TimeSpan.Zero)
            {
                offset = TimeSpan.Zero;
            }

            // Cap playback only if the schedule shortened this slot. The continuous chain never does; this stays
            // as a defensive bound so a schedule bug cannot make an item overrun its slot forever.
            var scheduled = slot.Stop - slot.Start;
            var full = TimeSpan.FromTicks(slot.Program.DurationTicks) + slot.Lead;
            TimeSpan? limit = scheduled < full - TimeSpan.FromSeconds(1) ? scheduled - offset : null;
            if (limit is { } value && value <= TimeSpan.Zero)
            {
                limit = null;
            }

            return new StreamStep(slot.Program, offset, limit, slot.Lead);
        }
    }

    public (ProgramEntry Program, DateTime StartUtc)? Following(int ahead)
        => _pos + ahead < _window.Count ? (_window[_pos + ahead].Program, _window[_pos + ahead].Start) : null;

    public void Advance()
    {
        _joinUtc = null;
        var end = _window[_pos].Stop;
        if (_follower?.TryTakeNewer(RefreshWait) is { } fresh)
        {
            // The loop was rebuilt: walk the new chain from scratch and join it at this boundary, partway into
            // a slot if the rebuilt timeline does not happen to have a boundary here too.
            _pool = fresh;
            _channel = _follower.Channel;
            _chain = null;
            Refill(end);
            JoinAt(end);
            return;
        }

        _pos++;
        if (_pos >= _window.Count)
        {
            Refill(end);
        }
    }

    // Positions on the slot airing at the given guide time and remembers the join point for its offset. A slot
    // that has all but ended there is skipped, so a join on a boundary never plays a sliver of the previous item.
    private void JoinAt(DateTime joinUtc)
    {
        while (_pos < _window.Count - 1 && StreamScheduleMath.IsSpent(_window[_pos].Stop - joinUtc))
        {
            _pos++;
        }

        _joinUtc = joinUtc;
    }

    private void Refill(DateTime fromUtc)
    {
        var (schedule, chain) = _channels.BuildTimelineResumable(_channel, _pool, fromUtc, fromUtc + Horizon, _chain);
        _chain = chain;
        _window = schedule.ToList();
        if (_window.Count == 0)
        {
            // Degenerate (a non-empty pool always yields at least one slot): fall back to the raw pool, untruncated.
            var lead = TimeSpan.FromTicks(ProgramLoop.LeadOf(_pool));
            _window = _pool.Select(p => new ScheduledProgram(p, fromUtc, fromUtc + TimeSpan.FromTicks(p.DurationTicks) + lead) { Lead = lead }).ToList();
        }

        _pos = 0;
    }
}
