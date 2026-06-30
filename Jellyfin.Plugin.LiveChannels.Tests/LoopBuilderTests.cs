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
        // A two-part episode is one block even at block size 1 (the block extends by one to hold the pair), so the
        // series' single shuffled block contains both parts adjacently.
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "The Trap (1)"),
            Ep(s, "Show", 1, 2, "The Trap (2)")
        }, Opts(block: 1, shuffle: true));

        Assert.Equal(new[] { 1, 2 }, EpisodeOrder(loop, s).ToArray()); // both parts, in order, adjacent
    }

    [Fact]
    public void MultiPart_BlockExtendsByAtMostOne_ThirdPartNotGlued()
    {
        // A three-parter (1)(2)(3) with block size 2: a block holds the pair (1)(2) but never a third part, so no
        // block exceeds blockSize + 1. The series' single shuffled block therefore never contains all three.
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "The Saga (1)"),
            Ep(s, "Show", 1, 2, "The Saga (2)"),
            Ep(s, "Show", 1, 3, "The Saga (3)")
        }, Opts(block: 2, shuffle: true));

        Assert.True(loop.Count <= 2, "a block glued more than a pair together: " + loop.Count);
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
    public void Shuffle_CapsEachSeriesToOneBlock_NoDomination()
    {
        // A huge series (40 episodes) plus four smaller ones (20 each), 4-episode blocks. The per-series cap means
        // every series contributes exactly ONE block (4 episodes) per loop -- so the giant series gets the same
        // footing as the others, the loop is 5 blocks (20 episodes), and nothing plays back to back.
        var items = new List<ProgramEntry>();
        var big = new Guid("11111111-1111-1111-1111-111111111111");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(big, "Futurama", 1, i, "e" + i)));
        for (var s = 0; s < 4; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(block: 4, shuffle: true));

        Assert.Equal(5 * 4, loop.Count);                                       // 5 series x one 4-episode block
        Assert.Equal(4, loop.Count(e => e.SeriesId == big));                   // the giant series is capped to 4
        Assert.True(MaxRun(loop) <= 4, "a series ran longer than one block: " + MaxRun(loop));
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
    public void Shuffle_KeepsEveryMovie_ButCapsSeriesToOneBlock()
    {
        // Movies are one-offs and all stay in the loop; a series is capped to a single block per loop (its other
        // blocks rotate in on later refreshes), so a 5-episode show contributes one block, not all five episodes.
        var s = Guid.NewGuid();
        var items = new List<ProgramEntry> { Movie("Zed"), Movie("Abe") };
        items.AddRange(Enumerable.Range(1, 5).Select(i => Ep(s, "Show", 1, i, "e" + i)));

        var loop = ProgramLoopBuilder.Build(items, Opts(block: 3, shuffle: true));

        Assert.Equal(2, loop.Count(e => e.SeriesId is null));   // both movies kept
        var showEps = loop.Count(e => e.SeriesId == s);
        Assert.True(showEps >= 1 && showEps < 5, "series should contribute one block, not its whole catalogue: " + showEps);
    }

    [Fact]
    public void Shuffle_SeriesBlockRotatesWithRotationCounter()
    {
        // The single block a series contributes advances with the rotation counter, so the channel works through
        // the series over successive refreshes instead of replaying the same episodes forever.
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 12).Select(i => Ep(s, "Show", 1, i, "e" + i)).ToList();

        var day0 = ProgramLoopBuilder.Build(items, new ChannelLoopOptions(4, true, true, false, "ch1", Rotation: 0));
        var day1 = ProgramLoopBuilder.Build(items, new ChannelLoopOptions(4, true, true, false, "ch1", Rotation: 1));

        Assert.NotEqual(day0.Select(e => e.EpisodeNumber), day1.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public void Movies_OrderAlphabetically_WhenNotShuffled()
    {
        var loop = ProgramLoopBuilder.Build(new[] { Movie("Zed"), Movie("Abe"), Movie("Mid") }, Opts());
        Assert.Equal(new[] { "Abe", "Mid", "Zed" }, loop.Select(e => e.Title).ToArray());
    }
}
