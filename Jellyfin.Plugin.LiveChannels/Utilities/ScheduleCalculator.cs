using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Turns a channel's looped item list into a wall-clock schedule. The schedule is a pure function of the
/// item list and the clock, anchored to a fixed epoch, so the guide and the live stream independently
/// compute the same "what is on now" without sharing state.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>The fixed anchor every channel's loop is measured from, so schedules are stable across restarts.</summary>
    public static readonly DateTime Epoch = DateTime.UnixEpoch;

    /// <summary>A hard cap on projected programmes so a channel of very short items can't produce an unbounded guide.</summary>
    private const int MaxProgrammes = 10000;

    /// <summary>
    /// Finds which item is airing at <paramref name="nowUtc"/> and how far into it the clock is.
    /// </summary>
    /// <param name="programs">The looped item list. Every entry must have a positive duration.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <param name="epochUtc">The loop anchor.</param>
    /// <returns>The index of the airing item and the offset into it, or <c>(0, zero)</c> when the list is empty.</returns>
    public static (int Index, TimeSpan Offset) CurrentProgram(IReadOnlyList<ProgramEntry> programs, DateTime nowUtc, DateTime epochUtc)
    {
        ArgumentNullException.ThrowIfNull(programs);
        if (programs.Count == 0)
        {
            return (0, TimeSpan.Zero);
        }

        long cycle = 0;
        foreach (var p in programs)
        {
            cycle += p.DurationTicks;
        }

        if (cycle <= 0)
        {
            return (0, TimeSpan.Zero);
        }

        var elapsed = (nowUtc - epochUtc).Ticks;
        var position = ((elapsed % cycle) + cycle) % cycle;

        long accumulated = 0;
        for (var i = 0; i < programs.Count; i++)
        {
            var dur = programs[i].DurationTicks;
            if (position < accumulated + dur)
            {
                return (i, TimeSpan.FromTicks(position - accumulated));
            }

            accumulated += dur;
        }

        // Floating accumulation can leave position exactly at the cycle end; treat as the start of item 0.
        return (0, TimeSpan.Zero);
    }

    /// <summary>
    /// Projects the schedule from the currently airing item forward until <paramref name="endUtc"/>.
    /// The first entry starts in the past (the airing item's true start) so the guide always has a "now".
    /// </summary>
    /// <param name="programs">The looped item list. Every entry must have a positive duration.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <param name="endUtc">The exclusive UTC end of the projection window.</param>
    /// <param name="epochUtc">The loop anchor.</param>
    /// <returns>The ordered, contiguous programmes covering <c>[nowStart, endUtc)</c>.</returns>
    public static IReadOnlyList<ScheduledProgram> BuildSchedule(
        IReadOnlyList<ProgramEntry> programs,
        DateTime nowUtc,
        DateTime endUtc,
        DateTime epochUtc)
    {
        ArgumentNullException.ThrowIfNull(programs);

        var schedule = new List<ScheduledProgram>();
        if (programs.Count == 0 || endUtc <= nowUtc)
        {
            return schedule;
        }

        var (index, offset) = CurrentProgram(programs, nowUtc, epochUtc);
        var start = nowUtc - offset;

        while (start < endUtc && schedule.Count < MaxProgrammes)
        {
            var program = programs[index];
            if (program.DurationTicks <= 0)
            {
                break;
            }

            var stop = start + TimeSpan.FromTicks(program.DurationTicks);
            schedule.Add(new ScheduledProgram(program, start, stop));
            start = stop;
            index = (index + 1) % programs.Count;
        }

        return schedule;
    }
}
