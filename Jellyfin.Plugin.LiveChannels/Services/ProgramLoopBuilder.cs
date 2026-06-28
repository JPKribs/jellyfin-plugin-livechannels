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
/// <param name="FavorKind">A content type to weight more heavily in the shuffled loop, or <see cref="Models.FavorKind.None"/>.</param>
/// <param name="FavorStrength">How strongly <paramref name="FavorKind"/> is favoured.</param>
public readonly record struct ChannelLoopOptions(
    int EpisodesPerBlock,
    bool KeepMultiPartTogether,
    bool Shuffle,
    bool ShuffleEpisodes,
    string ChannelId,
    FavorKind FavorKind = FavorKind.None,
    FavorStrength FavorStrength = FavorStrength.Moderate);

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

        if (!options.Shuffle)
        {
            return blocks
                .OrderBy(b => b.SortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Seq)
                .SelectMany(b => b.Items)
                .ToList();
        }

        // Spread each series' blocks evenly across the loop rather than shuffling every block independently:
        // a flat shuffle lets a series with many blocks clump into back-to-back runs. Each block is placed at a
        // fractional position (index + phase) / blockCount within its series, where phase is a stable per-series
        // hash so equal-sized series do not line up. Sorting by that position deals each series out at even
        // intervals and interleaves the series in proportion to their size, while keeping a series in order.
        // Standalone items are one-block groups, so each lands at its own random phase and scatters too.
        // When the channel favours a content type, give every group of that type extra slots so it fills the
        // target share of the loop, cycling its blocks to cover the slots (the "repeat to fill the share" choice).
        // A multiplier of 1 leaves a group untouched.
        var favorMultiplier = FavorMultiplier(blocks, options);

        var spread = blocks
            .GroupBy(b => b.GroupKey, StringComparer.Ordinal)
            .SelectMany(g =>
            {
                var ordered = g.OrderBy(b => b.Seq).ToList();
                var phase = (uint)ShuffleKey(options.ChannelId, "phase:" + g.Key) / (double)uint.MaxValue;
                var favored = options.FavorKind != FavorKind.None && KindOf(ordered[0]) == options.FavorKind;
                var slots = favored ? Math.Max(ordered.Count, (int)Math.Round(ordered.Count * favorMultiplier)) : ordered.Count;
                return Enumerable.Range(0, slots).Select(k => (Block: ordered[k % ordered.Count], Position: (k + phase) / slots));
            })
            .OrderBy(x => x.Position)
            .ThenBy(x => x.Block.GroupKey, StringComparer.Ordinal)
            .Select(x => x.Block)
            .ToList();

        SpaceOutNeighbours(spread);
        return spread.SelectMany(b => b.Items).ToList();
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

    // Pulls apart any same-series blocks the even spread still left adjacent (a series large enough to dominate
    // the channel). For each clash, swaps the second block forward with the nearest later block when that strictly
    // reduces the local number of same-series adjacencies, so it never makes the order worse and leaves a pair in
    // place only when the series genuinely fills the rest of the loop. Deterministic, so guide and stream agree.
    private static void SpaceOutNeighbours(List<Block> order)
    {
        for (var i = 1; i < order.Count; i++)
        {
            if (!SameSeries(order[i], order[i - 1]))
            {
                continue;
            }

            for (var j = i + 1; j < order.Count; j++)
            {
                if (SameSeries(order[j], order[i]))
                {
                    continue;
                }

                var before = LocalAdjacencies(order, i, j);
                (order[i], order[j]) = (order[j], order[i]);
                if (LocalAdjacencies(order, i, j) < before)
                {
                    break;
                }

                (order[i], order[j]) = (order[j], order[i]);
            }
        }
    }

    private static bool SameSeries(Block a, Block b) => string.Equals(a.GroupKey, b.GroupKey, StringComparison.Ordinal);

    // The content type of a block, from its first item: an episode (has a series), a movie, or otherwise a
    // standalone (music video).
    private static FavorKind KindOf(Block block)
    {
        var item = block.Items[0];
        return item.SeriesId is not null ? FavorKind.Shows : (item.IsMovie ? FavorKind.Movies : FavorKind.MusicVideos);
    }

    // How many times to inflate the favoured type's slots so it reaches its target share of the loop. Returns 1
    // (no change) when nothing is favoured, the type is absent, the type is already the whole channel, or it is
    // already above its target. Capped so a tiny favoured library does not repeat absurdly often.
    private static double FavorMultiplier(List<Block> blocks, ChannelLoopOptions options)
    {
        if (options.FavorKind == FavorKind.None)
        {
            return 1.0;
        }

        var favored = blocks.Count(b => KindOf(b) == options.FavorKind);
        var others = blocks.Count - favored;
        if (favored == 0 || others == 0)
        {
            return 1.0;
        }

        var target = options.FavorStrength switch
        {
            FavorStrength.Slight => 0.45,
            FavorStrength.Heavy => 0.85,
            _ => 0.65
        };

        if ((double)favored / blocks.Count >= target)
        {
            return 1.0;
        }

        // favored*m / (favored*m + others) = target  ->  m = target*others / (favored*(1-target))
        var m = target * others / (favored * (1 - target));
        return Math.Clamp(m, 1.0, 10.0);
    }

    // Counts the same-series adjacent pairs touching positions a and b (the only pairs a swap of a and b changes).
    // a < b always, so the only overlapping right-index is a+1 == b.
    private static int LocalAdjacencies(List<Block> order, int a, int b)
    {
        var count = Pair(order, a) + Pair(order, b) + Pair(order, b + 1);
        if (a + 1 != b)
        {
            count += Pair(order, a + 1);
        }

        return count;
    }

    // 1 when the block at `right` shares a series with the block before it, else 0.
    private static int Pair(List<Block> order, int right)
        => right >= 1 && right < order.Count && SameSeries(order[right], order[right - 1]) ? 1 : 0;

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
