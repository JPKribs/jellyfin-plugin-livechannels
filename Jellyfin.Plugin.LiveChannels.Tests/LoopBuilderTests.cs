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
        => new ProgramEntry(Guid.NewGuid(), title, null, Hour, "/m.mkv");

    private static ProgramEntry Ep(Guid seriesId, string seriesName, int season, int number, string rawName)
        => new ProgramEntry(Guid.NewGuid(), seriesName + " - " + rawName, null, Hour, "/e.mkv")
        {
            SeriesId = seriesId,
            SeriesName = seriesName,
            SeasonNumber = season,
            EpisodeNumber = number,
            RawName = rawName
        };

    private static ChannelLoopOptions Opts(int block = 1, bool keepMulti = true, bool shuffle = false, bool shuffleEp = false, string ch = "ch1")
        => new ChannelLoopOptions(block, keepMulti, shuffle, shuffleEp, ch);

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
