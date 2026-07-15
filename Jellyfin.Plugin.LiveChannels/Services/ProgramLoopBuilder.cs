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
/// <param name="Mode">How block order is arranged: shuffle (deterministically), alphabetical by name, or chronological by date.</param>
/// <param name="ShuffleEpisodes">Shuffle episodes within a series instead of playing them in air order.</param>
/// <param name="ChannelId">Channel id, seeding the deterministic shuffle so the guide and stream agree.</param>
/// <param name="FavorKind">A content type to weight more heavily in the shuffled loop, or <see cref="Models.FavorKind.None"/>.</param>
/// <param name="FavorStrength">How strongly <paramref name="FavorKind"/> is favoured.</param>
/// <param name="Rotation">A counter (e.g. days since an epoch) that advances which single block each series
/// contributes in a shuffled loop, so the channel walks through a series over time. Stable within a built
/// schedule (guide and stream agree); the caller bumps it each refresh.</param>
public readonly record struct ChannelLoopOptions(
    int EpisodesPerBlock,
    bool KeepMultiPartTogether,
    LoopMode Mode,
    bool ShuffleEpisodes,
    string ChannelId,
    FavorKind FavorKind = FavorKind.None,
    FavorStrength FavorStrength = FavorStrength.Moderate,
    int Rotation = 0);

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

        if (options.Mode != LoopMode.Shuffle)
        {
            // Chronological orders blocks by their earliest release/air date; alphabetical (the default non-shuffle
            // order) by title. Both fall back to name then sequence so the order is stable and series stay in order.
            var ordered = options.Mode == LoopMode.Chronological
                ? blocks.OrderBy(BlockDate).ThenBy(b => b.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Seq)
                : blocks.OrderBy(b => b.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Seq);

            return ordered.SelectMany(b => b.Items).ToList();
        }

        // Each series contributes ONE block per loop, so One Piece and a twelve-episode show get equal footing and
        // no series can dominate. Which block is the series' "current" one rotates with options.Rotation (a per-
        // series offset keeps series from syncing), so the loop walks through each series over successive refreshes.
        // A favoured content type is the one exception: its groups contribute a few blocks (a rotating window) so
        // that type fills more of the loop. The chosen blocks are then ordered round-robin -- one block from each
        // series per round, dealt in a stable per-channel order so a series never straddles a round boundary and the
        // same show never plays twice in a row. Standalone items (movies, videos) are single blocks scattered across
        // the rounds by their phase. Deterministic (seeded by channel id) so the guide projection and stream agree.
        var favorMultiplier = FavorMultiplier(blocks, options);

        var groups = blocks
            .GroupBy(b => b.GroupKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var all = g.OrderBy(b => b.Seq).ToList();
                var favored = options.FavorKind != FavorKind.None && KindOf(all[0]) == options.FavorKind;
                // One block per series; a favoured type takes more (a rotating window, repeating its blocks if
                // needed) so that type fills more of the loop.
                var take = favored ? Math.Max(1, (int)Math.Round(favorMultiplier)) : 1;
                var offset = (int)((uint)ShuffleKey(options.ChannelId, "rot:" + g.Key) % (uint)all.Count);
                var start = (((options.Rotation + offset) % all.Count) + all.Count) % all.Count;
                var window = new List<Block>(take);
                for (var k = 0; k < take; k++)
                {
                    window.Add(all[(start + k) % all.Count]);
                }

                var phase = (uint)ShuffleKey(options.ChannelId, "phase:" + g.Key) / (double)uint.MaxValue;
                return (Ordered: window, Slots: take, Phase: phase);
            })
            .ToList();

        var maxRounds = groups.Max(g => g.Slots);
        var placements = new List<(int Round, int Order, Block Block)>();
        foreach (var g in groups)
        {
            // A stable per-series order used in EVERY round, so each round deals the series in the same sequence.
            // That keeps the same series from straddling a round boundary (round N ends on a different series than
            // round N+1 begins) -- the only same-series run is the legitimate tail once all other series run out.
            var order = ShuffleKey(options.ChannelId, "order:" + g.Ordered[0].GroupKey);
            if (g.Slots <= 1)
            {
                // A once-only item (a movie or video): drop it into a round chosen by its phase so the standalone
                // items scatter through the loop instead of all landing in the first round.
                var round = Math.Min(maxRounds - 1, (int)(g.Phase * maxRounds));
                placements.Add((round, order, g.Ordered[0]));
            }
            else
            {
                for (var r = 0; r < g.Slots; r++)
                {
                    placements.Add((r, order, g.Ordered[r % g.Ordered.Count]));
                }
            }
        }

        return placements
            .OrderBy(p => p.Round)
            .ThenBy(p => p.Order)
            .ThenBy(p => p.Block.GroupKey, StringComparer.Ordinal)
            .SelectMany(p => p.Block.Items)
            .ToList();
    }

    // Groups consecutive episodes that share a base title and a part marker into one unit, so a two-parter is kept
    // together (a block extends by at most one episode to hold the pair). A unit is capped at two episodes: a
    // three-parter (1)(2)(3) keeps (1)(2) together and lets (3) fall into the next block -- we never extend a block
    // by more than one. Every other episode is its own single-item unit.
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
            // Join the run only to complete a pair (cap at two episodes), so (1)(2) stay together but (3) does not.
            var partBase = MultiPartBase(e.RawName);
            if (partBase is not null && runBase is not null && run!.Count < 2 && string.Equals(partBase, runBase, StringComparison.OrdinalIgnoreCase))
            {
                run.Add(e);
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

    // The content type of a block, from its first item: an episode (has a series), a movie, or otherwise a
    // standalone (music video).
    private static FavorKind KindOf(Block block)
    {
        var item = block.Items[0];
        return item.SeriesId is not null ? FavorKind.Shows : (item.IsMovie ? FavorKind.Movies : FavorKind.MusicVideos);
    }

    // A block's chronological key: the earliest release/air date across its items (a production-only year counts as
    // its 1 January), so undated content sorts last.
    private static DateTime BlockDate(Block block)
    {
        var earliest = DateTime.MaxValue;
        foreach (var item in block.Items)
        {
            var date = item.PremiereDate ?? (item.Year is int year ? new DateTime(year, 1, 1) : (DateTime?)null);
            if (date is { } value && value < earliest)
            {
                earliest = value;
            }
        }

        return earliest;
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
