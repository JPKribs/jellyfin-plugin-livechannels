using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="ProgramLoopBuilder"/> — block grouping, multi-part keeping, and deterministic order.
/// </summary>
public class LoopBuilderTests
{
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    private static ProgramEntry Movie(string title)
        => new ProgramEntry(Guid.NewGuid(), title, null, Hour, "/m.mkv") { IsMovie = true };

    private static ProgramEntry Ep(Guid seriesId, string seriesName, int season, int number, string rawName)
        => new ProgramEntry(Guid.NewGuid(), seriesName + " - " + rawName, null, Hour, "/e.mkv")
        {
            SeriesId = seriesId,
            SeriesName = seriesName,
            SeasonNumber = season,
            EpisodeNumber = number,
            RawName = rawName
        };

    private static ChannelLoopOptions Opts(int block = 1, bool keepMulti = true, bool shuffle = false, bool shuffleEp = false, string ch = "ch1", FavorKind favor = FavorKind.None, FavorStrength strength = FavorStrength.Moderate)
        => new ChannelLoopOptions(block, keepMulti, shuffle, shuffleEp, ch, favor, strength);

    // Episode numbers of one series, in output order.
    private static List<int> EpisodeOrder(IReadOnlyList<ProgramEntry> loop, Guid seriesId)
        => loop.Where(e => e.SeriesId == seriesId).Select(e => e.EpisodeNumber ?? -1).ToList();

    // Asserts a series' episodes occupy a contiguous run of the loop in the given episode order.
    private static void AssertContiguous(IReadOnlyList<ProgramEntry> loop, Guid seriesId, int[] expected)
    {
        var indices = new List<int>();
        for (var i = 0; i < loop.Count; i++)
        {
            if (loop[i].SeriesId == seriesId)
            {
                indices.Add(i);
            }
        }

        Assert.Equal(expected.Length, indices.Count);
        Assert.Equal(indices.Count - 1, indices[^1] - indices[0]); // contiguous
        Assert.Equal(expected, EpisodeOrder(loop, seriesId).ToArray());
    }

    [Fact]
    public void Empty_ReturnsEmpty()
        => Assert.Empty(ProgramLoopBuilder.Build(Array.Empty<ProgramEntry>(), Opts()));

    [Fact]
    public void Episodes_PlayInAirOrder()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 3, "C"), Ep(s, "Show", 1, 1, "A"), Ep(s, "Show", 1, 2, "B")
        }, Opts());

        Assert.Equal(new[] { 1, 2, 3 }, EpisodeOrder(loop, s).ToArray());
    }

    [Fact]
    public void Blocks_KeepSeriesContiguousAndInOrder_WhenShuffled()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var items = new List<ProgramEntry>();
        for (var i = 1; i <= 4; i++)
        {
            items.Add(Ep(a, "Alpha", 1, i, "a" + i));
            items.Add(Ep(b, "Bravo", 1, i, "b" + i));
        }

        // Block size = series length, shuffled: each series is one block, so its episodes stay contiguous.
        var loop = ProgramLoopBuilder.Build(items, Opts(block: 4, shuffle: true));

        Assert.Equal(8, loop.Count);
        AssertContiguous(loop, a, new[] { 1, 2, 3, 4 });
        AssertContiguous(loop, b, new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void MultiPart_StaysAdjacent_EvenAtBlockSizeOne()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "Intro"),
            Ep(s, "Show", 1, 2, "The Trap (1)"),
            Ep(s, "Show", 1, 3, "The Trap (2)"),
            Ep(s, "Show", 1, 4, "End")
        }, Opts(block: 1, shuffle: true));

        var order = EpisodeOrder(loop, s);
        var p1 = order.IndexOf(2);
        var p2 = order.IndexOf(3);
        Assert.Equal(p1 + 1, p2); // (1) immediately followed by (2)
    }

    [Fact]
    public void MultiPart_StaysAdjacent_WhenEpisodesShuffled()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "One"),
            Ep(s, "Show", 1, 2, "Big Story - Part 1"),
            Ep(s, "Show", 1, 3, "Big Story - Part 2"),
            Ep(s, "Show", 1, 4, "Four"),
            Ep(s, "Show", 1, 5, "Five")
        }, Opts(block: 1, shuffleEp: true));

        var order = EpisodeOrder(loop, s);
        Assert.Equal(order.IndexOf(2) + 1, order.IndexOf(3));
    }

    [Fact]
    public void Shuffle_IsDeterministic()
    {
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 6).Select(i => Ep(s, "Show", 1, i, "e" + i)).ToList();

        var a = ProgramLoopBuilder.Build(items, Opts(block: 2, shuffle: true));
        var b = ProgramLoopBuilder.Build(items, Opts(block: 2, shuffle: true));

        Assert.Equal(a.Select(e => e.ItemId), b.Select(e => e.ItemId));
    }

    [Fact]
    public void Shuffle_EqualSeries_RoundRobin_NoBackToBackBlocks()
    {
        // Five equal-sized series, 4-episode blocks. Round-robin must interleave them perfectly, so no series ever
        // plays two blocks back to back -- the longest same-series run is a single block (4 episodes).
        var items = new List<ProgramEntry>();
        for (var s = 0; s < 5; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(block: 4, shuffle: true));

        Assert.True(MaxRun(loop) <= 4, "a series ran longer than one 4-episode block: " + MaxRun(loop));
    }

    [Fact]
    public void Shuffle_DominantSeries_NeverRepeatsUntilOthersExhausted()
    {
        // A dominant series (10 blocks) plus four smaller ones (5 blocks each), 4-episode blocks. The rule: a
        // series never recurs until every other series has had a block. So while the small series still have
        // blocks, nothing plays back to back; only once they are exhausted may the dominant series fill the tail.
        var items = new List<ProgramEntry>();
        var big = new Guid("11111111-1111-1111-1111-111111111111");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(big, "Futurama", 1, i, "e" + i)));
        for (var s = 0; s < 4; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(block: 4, shuffle: true));

        // Find where the small series are exhausted (the last episode that is not the dominant series). Up to and
        // including that point, no series may run longer than one block; after it, the dominant tail is allowed.
        var lastOther = loop.Count - 1;
        while (lastOther >= 0 && loop[lastOther].SeriesId == big)
        {
            lastOther--;
        }

        Assert.True(MaxRun(loop.Take(lastOther + 1).ToList()) <= 4, "a series clumped before the others were exhausted: " + MaxRun(loop.Take(lastOther + 1).ToList()));
        // Every episode is still present (nothing dropped), just reordered.
        Assert.Equal(items.Count, loop.Count);
    }

    // The longest run of consecutive items sharing a series.
    private static int MaxRun(IReadOnlyList<ProgramEntry> loop)
    {
        var maxRun = loop.Count > 0 ? 1 : 0;
        var run = 1;
        for (var i = 1; i < loop.Count; i++)
        {
            run = loop[i].SeriesId == loop[i - 1].SeriesId ? run + 1 : 1;
            maxRun = Math.Max(maxRun, run);
        }

        return maxRun;
    }

    [Fact]
    public void Favor_BoostsChosenKind_ByRepeating()
    {
        // Four movies against a 40-episode show. Naturally the show dominates; favoring movies should multiply
        // their airtime by repeating them, so movies become at least as common as the show.
        var items = new List<ProgramEntry>();
        for (var i = 0; i < 4; i++)
        {
            items.Add(Movie("Movie" + i));
        }

        var show = new Guid("33333333-3333-3333-3333-333333333333");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(show, "Show", 1, i, "e" + i)));

        int Movies(IReadOnlyList<ProgramEntry> loop) => loop.Count(e => e.SeriesId is null);

        var plain = ProgramLoopBuilder.Build(items, Opts(block: 1, shuffle: true));
        var favored = ProgramLoopBuilder.Build(items, Opts(block: 1, shuffle: true, favor: FavorKind.Movies, strength: FavorStrength.Heavy));

        Assert.Equal(4, Movies(plain));                                   // natural: each movie once
        Assert.True(Movies(favored) >= Movies(plain) * 5, "favoring should multiply movie airtime");
        Assert.True(Movies(favored) >= favored.Count(e => e.SeriesId is not null), "favored movies should be at least as common as the show");
    }

    [Fact]
    public void AllItems_ArePreserved()
    {
        var s = Guid.NewGuid();
        var items = new List<ProgramEntry> { Movie("Zed"), Movie("Abe") };
        items.AddRange(Enumerable.Range(1, 5).Select(i => Ep(s, "Show", 1, i, "e" + i)));

        var loop = ProgramLoopBuilder.Build(items, Opts(block: 3, shuffle: true));

        Assert.Equal(7, loop.Count);
        Assert.Equal(items.Select(i => i.ItemId).OrderBy(x => x), loop.Select(i => i.ItemId).OrderBy(x => x));
    }

    [Fact]
    public void Movies_OrderAlphabetically_WhenNotShuffled()
    {
        var loop = ProgramLoopBuilder.Build(new[] { Movie("Zed"), Movie("Abe"), Movie("Mid") }, Opts());
        Assert.Equal(new[] { "Abe", "Mid", "Zed" }, loop.Select(e => e.Title).ToArray());
    }
}
