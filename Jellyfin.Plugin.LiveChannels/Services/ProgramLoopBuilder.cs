using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Options controlling how a channel's resolved items are ordered into its looping schedule.
/// </summary>
/// <param name="EpisodesPerBlock">Consecutive episodes of a series to play before moving on (minimum 1).</param>
/// <param name="KeepMultiPartTogether">Keep multi-part episodes adjacent and never split across a block.</param>
/// <param name="Shuffle">Shuffle block order (deterministically) instead of ordering by name.</param>
/// <param name="ShuffleEpisodes">Shuffle episodes within a series instead of playing them in air order.</param>
/// <param name="ChannelId">Channel id, seeding the deterministic shuffle so the guide and stream agree.</param>
public readonly record struct ChannelLoopOptions(
    int EpisodesPerBlock,
    bool KeepMultiPartTogether,
    bool Shuffle,
    bool ShuffleEpisodes,
    string ChannelId);

/// <summary>
/// Turns a channel's resolved items into the ordered loop it cycles through: episodes are grouped by series
/// (in air order, multi-parters kept whole), chunked into blocks, and the blocks are ordered for the channel.
/// Pure and deterministic so the guide projection and the live stream always agree.
/// </summary>
public static class ProgramLoopBuilder
{
    // Trailing part markers: "Title (2)", "Title [2]", "Title - Part 2", "Title Pt. 2".
    private static readonly Regex PartSuffix = new(
        @"^(?<base>.*?)[\s:\-]*(?:\((?<n1>\d{1,2})\)|\[(?<n2>\d{1,2})\]|(?:part|pt\.?)\s*(?<n3>\d{1,2}))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds the ordered program loop.
    /// </summary>
    /// <param name="items">The channel's resolved items.</param>
    /// <param name="options">The ordering options.</param>
    /// <returns>The ordered loop.</returns>
    public static IReadOnlyList<ProgramEntry> Build(IReadOnlyList<ProgramEntry> items, ChannelLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Array.Empty<ProgramEntry>();
        }

        var blockSize = Math.Max(1, options.EpisodesPerBlock);
        var blocks = new List<Block>();

        // Standalone items (movies, videos) each become a one-item block.
        foreach (var item in items.Where(i => i.SeriesId is null))
        {
            blocks.Add(new Block("item:" + item.ItemId.ToString("N"), item.Title ?? string.Empty, 0, new List<ProgramEntry> { item }));
        }

        // Episodes are grouped per series, ordered, split into multi-part-aware units, then chunked.
        foreach (var series in items.Where(i => i.SeriesId is not null).GroupBy(i => i.SeriesId!.Value))
        {
            var ordered = series
                .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
                .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
                .ThenBy(e => e.RawName ?? e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var units = GroupUnits(ordered, options.KeepMultiPartTogether);

            if (options.ShuffleEpisodes)
            {
                var seriesKey = series.Key.ToString("N");
                units = units
                    .OrderBy(u => ShuffleKey(options.ChannelId, seriesKey + ":" + u[0].ItemId.ToString("N")))
                    .ToList();
            }

            var name = ordered[0].SeriesName ?? ordered[0].Title ?? string.Empty;
            var groupKey = "series:" + series.Key.ToString("N");
            var seq = 0;
            var current = new List<ProgramEntry>();
            foreach (var unit in units)
            {
                current.AddRange(unit);
                if (current.Count >= blockSize)
                {
                    blocks.Add(new Block(groupKey, name, seq++, current));
                    current = new List<ProgramEntry>();
                }
            }

            if (current.Count > 0)
            {
                blocks.Add(new Block(groupKey, name, seq, current));
            }
        }

        var orderedBlocks = options.Shuffle
            ? blocks
                .OrderBy(b => ShuffleKey(options.ChannelId, b.GroupKey + "#" + b.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ThenBy(b => b.GroupKey, StringComparer.Ordinal)
            : blocks
                .OrderBy(b => b.SortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Seq);

        return orderedBlocks.SelectMany(b => b.Items).ToList();
    }

    // Groups consecutive episodes that share a base title and a part marker into one unit (so a two-parter
    // is never split). Every other episode is its own single-item unit.
    private static List<List<ProgramEntry>> GroupUnits(List<ProgramEntry> ordered, bool keepMultiPart)
    {
        var units = new List<List<ProgramEntry>>();
        if (!keepMultiPart)
        {
            foreach (var e in ordered)
            {
                units.Add(new List<ProgramEntry> { e });
            }

            return units;
        }

        List<ProgramEntry>? run = null;
        string? runBase = null;

        foreach (var e in ordered)
        {
            var partBase = MultiPartBase(e.RawName);
            if (partBase is not null && runBase is not null && string.Equals(partBase, runBase, StringComparison.OrdinalIgnoreCase))
            {
                run!.Add(e);
                continue;
            }

            if (run is not null)
            {
                units.Add(run);
            }

            if (partBase is not null)
            {
                run = new List<ProgramEntry> { e };
                runBase = partBase;
            }
            else
            {
                units.Add(new List<ProgramEntry> { e });
                run = null;
                runBase = null;
            }
        }

        if (run is not null)
        {
            units.Add(run);
        }

        return units;
    }

    // Returns the title with its trailing part marker stripped (e.g. "The Trap (1)" -> "The Trap"), or null
    // when the name has no part marker. A short, non-empty base guards against false matches.
    private static string? MultiPartBase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = PartSuffix.Match(name);
        if (!match.Success)
        {
            return null;
        }

        var baseTitle = match.Groups["base"].Value.Trim();
        return baseTitle.Length >= 2 ? baseTitle : null;
    }

    // FNV-1a hash of channel id + key, giving a stable per-channel ordering that survives restarts.
    private static int ShuffleKey(string channelId, string key)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in channelId)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            foreach (var c in key)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            return (int)hash;
        }
    }

    private sealed record Block(string GroupKey, string SortName, int Seq, List<ProgramEntry> Items);
}
