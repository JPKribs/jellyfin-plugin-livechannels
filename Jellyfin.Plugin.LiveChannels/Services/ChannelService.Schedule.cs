using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: projecting a resolved channel onto the wall clock -- the free-running loop for ordinary
// channels, or a time-of-day-aware daypart schedule for channels that carry custom rating blocks.
public partial class ChannelService
{
    /// <summary>
    /// Projects a channel's resolved loop onto wall-clock time for <c>[fromUtc, toUtc)</c>. Ordinary channels use
    /// the free-running epoch loop; a channel with time-of-day rating blocks uses a daypart-aware schedule so the
    /// content that airs respects the rating window active then.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="programs">The channel's resolved loop (from <see cref="ResolvePrograms"/> or <see cref="RefreshPrograms"/>).</param>
    /// <param name="fromUtc">The inclusive UTC start of the window.</param>
    /// <param name="toUtc">The exclusive UTC end of the window.</param>
    /// <returns>The ordered programmes covering the window.</returns>
    public IReadOnlyList<ScheduledProgram> BuildTimeline(Channel channel, IReadOnlyList<ProgramEntry> programs, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(programs);

        var blocks = ResolveRatingBlocks(channel);
        if (!HasTimeOfDayRating(blocks))
        {
            return ScheduleCalculator.BuildSchedule(programs, fromUtc, toUtc, ScheduleCalculator.Epoch);
        }

        return DaypartSchedule.Build(programs, blocks, channel.TransitionWindowMinutes, TimeZoneInfo.Local, fromUtc, toUtc, channel.Id);
    }

    /// <summary>
    /// Whether the channel's rating limits vary by time of day (it carries at least one custom block), so it needs
    /// the daypart schedule and the per-item stream path rather than the free-running loop.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns><c>true</c> when the channel has a time-of-day rating block.</returns>
    internal bool IsTimeOfDayChannel(Channel channel) => HasTimeOfDayRating(ResolveRatingBlocks(channel));

    /// <summary>
    /// Whether a kids-tagged rating block is active at the given UTC instant (in the server's local time), so the
    /// guide can tag the program airing then as kids content.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="utc">The program's UTC start.</param>
    /// <returns><c>true</c> when a kids block covers that time.</returns>
    internal bool KidsActiveAt(Channel channel, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var minute = (local.Hour * 60) + local.Minute;
        foreach (var block in channel.EffectiveRatingBlocks())
        {
            if (block.IsKids && block.ActiveAt(minute))
            {
                return true;
            }
        }

        return false;
    }

    // Resolves a channel's configured rating blocks (rating names) into numeric windows for schedule maths.
    internal List<ResolvedRatingBlock> ResolveRatingBlocks(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var result = new List<ResolvedRatingBlock>();
        foreach (var block in channel.EffectiveRatingBlocks())
        {
            var window = new RatingWindow(
                ResolveRatingScore(block.MinOfficialRating),
                ResolveRatingScore(block.MaxOfficialRating),
                block.IncludeUnrated);
            result.Add(new ResolvedRatingBlock(
                window,
                block.Period == RatingBlockPeriod.AllDay,
                NormalizeMinute(block.StartMinutes),
                NormalizeMinute(block.EndMinutes)));
        }

        return result;
    }

    // Whether any block is time-of-day (custom); only then does the channel need the daypart schedule instead of
    // the free-running loop.
    private static bool HasTimeOfDayRating(IReadOnlyList<ResolvedRatingBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (!block.AllDay)
            {
                return true;
            }
        }

        return false;
    }

    // The single rating band for a channel whose blocks are all all-day: the overlap-combined window (lowest min,
    // lowest max, unrated only if all allow), applied once at build time exactly like the legacy min/max fields.
    private RatingFilter EffectiveSingleBandFilter(IReadOnlyList<ResolvedRatingBlock> blocks)
    {
        var window = RatingSchedule.EffectiveWindow(blocks, 0);
        return new RatingFilter(window.Min, window.Max, window.IncludeUnrated);
    }

    private static int NormalizeMinute(int minute)
        => ((minute % RatingSchedule.MinutesPerDay) + RatingSchedule.MinutesPerDay) % RatingSchedule.MinutesPerDay;
}
