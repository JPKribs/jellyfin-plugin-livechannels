using System;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the continuous-stream playlist rotation: the concat list starts at the item now airing (so the
/// tune-in seek stays inside the first file instead of spanning the whole loop) while the cyclic play order
/// is preserved exactly.
/// </summary>
public class ConcatRotationTests
{
    private static ProgramEntry Item(string title) => new(Guid.NewGuid(), title, null, 600_000_000L, "/media/" + title + ".mkv");

    [Fact]
    public void Rotate_PreservesCyclicOrder()
    {
        var items = new[] { Item("a"), Item("b"), Item("c"), Item("d") };

        var rotated = StreamSessionService.Rotate(items, 2).Select(p => p.Title).ToArray();

        Assert.Equal(new[] { "c", "d", "a", "b" }, rotated);
    }

    [Fact]
    public void Rotate_AtStart_IsIdentity()
    {
        var items = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Equal(
            items.Select(p => p.Title).ToArray(),
            StreamSessionService.Rotate(items, 0).Select(p => p.Title).ToArray());
    }
}
