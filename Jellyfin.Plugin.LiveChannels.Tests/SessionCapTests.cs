using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the concurrent-session cap: <see cref="LiveChannelsTvService.SelectCapVictims"/> closes the oldest
/// sessions first, never the one just opened, and only as many as the overflow above the cap.
/// </summary>
public class SessionCapTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnderCap_EvictsNothing()
    {
        var sessions = new List<(string, DateTime)>
        {
            ("a", Base),
            ("b", Base.AddMinutes(1)),
        };

        Assert.Empty(LiveChannelsTvService.SelectCapVictims(sessions, keep: "b", cap: 3));
    }

    [Fact]
    public void OverCap_EvictsOldestFirst()
    {
        var sessions = new List<(string, DateTime)>
        {
            ("old", Base),
            ("mid", Base.AddMinutes(5)),
            ("new", Base.AddMinutes(10)),
        };

        // Cap of 2 with three sessions: one must go, and it is the oldest.
        Assert.Equal(new[] { "old" }, LiveChannelsTvService.SelectCapVictims(sessions, keep: "new", cap: 2));
    }

    [Fact]
    public void NeverEvictsTheKeptSession()
    {
        // The just-opened session is the oldest here, but it is kept; the next-oldest is closed instead.
        var sessions = new List<(string, DateTime)>
        {
            ("keep", Base),
            ("other", Base.AddMinutes(5)),
        };

        Assert.Equal(new[] { "other" }, LiveChannelsTvService.SelectCapVictims(sessions, keep: "keep", cap: 1));
    }

    [Fact]
    public void ZeroCap_IsUnlimited()
    {
        var sessions = new List<(string, DateTime)>
        {
            ("a", Base),
            ("b", Base.AddMinutes(1)),
            ("c", Base.AddMinutes(2)),
        };

        Assert.Empty(LiveChannelsTvService.SelectCapVictims(sessions, keep: "c", cap: 0));
    }

    [Fact]
    public void EvictsAllOverflow_OldestFirst()
    {
        var sessions = new List<(string, DateTime)>
        {
            ("s1", Base),
            ("s2", Base.AddMinutes(1)),
            ("s3", Base.AddMinutes(2)),
            ("s4", Base.AddMinutes(3)),
        };

        // Cap of 1 with the newest kept: the three oldest are closed, oldest first.
        Assert.Equal(new[] { "s1", "s2", "s3" }, LiveChannelsTvService.SelectCapVictims(sessions, keep: "s4", cap: 1));
    }
}
