using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the producer restart budget: a run of quick failures is capped so a broken pipeline is not restarted
/// forever, but the cap counts consecutive failures, so a long-lived session keeps healing the occasional crash.
/// </summary>
public class RestartBudgetTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Healthy = TimeSpan.FromMinutes(10);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A session whose producer has already died; every replacement also "dies" immediately (a completed task).
    private static LiveChannelsTvService.LiveSession DeadSession()
        => new("lc_test", new CancellationTokenSource(), Task.CompletedTask, "/tmp/lc_test", "Test", "chan", 1, T0, new SessionStats());

    private static bool Restart(LiveChannelsTvService.LiveSession session, DateTime now)
        => session.TryRestart(Interval, 3, Healthy, _ => Task.CompletedTask, out _, now);

    // Stamps when the session's current (already completed) replacement stopped, as its continuation does live.
    private static void Ended(LiveChannelsTvService.LiveSession session, DateTime at) => session.MarkWorkerEnded(session.Worker, at);

    [Fact]
    public void QuickFailures_StopAtTheCap()
    {
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Assert.True(Restart(session, T0.AddSeconds(6)));
        Assert.True(Restart(session, T0.AddSeconds(12)));
        Assert.False(Restart(session, T0.AddSeconds(18)));
        Assert.Equal(3, session.Restarts);
    }

    [Fact]
    public void RestartsFasterThanTheInterval_AreRefused()
    {
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Assert.False(Restart(session, T0.AddSeconds(2)));
        Assert.Equal(1, session.Restarts);
    }

    [Fact]
    public void ARefusalForComingTooSoon_SaysWhenToComeBack()
    {
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Assert.False(session.TryRestart(Interval, 3, Healthy, _ => Task.CompletedTask, out var retryAfter, T0.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(3), retryAfter);
    }

    [Fact]
    public void AnExhaustedBudget_SaysNothingAboutComingBack()
    {
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Assert.True(Restart(session, T0.AddSeconds(6)));
        Assert.True(Restart(session, T0.AddSeconds(12)));
        Assert.False(session.TryRestart(Interval, 3, Healthy, _ => Task.CompletedTask, out var retryAfter, T0.AddSeconds(14)));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void TimePassing_DoesNotRefillTheBudget()
    {
        // Every replacement died at once; the viewer stayed tuned in and the reaper keeps asking. However long it
        // waits, quick failures never count as a healthy run, so the session is never restarted again.
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Ended(session, T0.AddSeconds(1));
        Assert.True(Restart(session, T0.AddSeconds(6)));
        Ended(session, T0.AddSeconds(7));
        Assert.True(Restart(session, T0.AddSeconds(12)));
        Ended(session, T0.AddSeconds(13));

        for (var minutes = 1; minutes <= 120; minutes++)
        {
            Assert.False(Restart(session, T0.AddMinutes(minutes)));
        }

        Assert.Equal(3, session.Restarts);
    }

    [Fact]
    public void AHealthyRun_ClearsTheCount()
    {
        var session = DeadSession();
        Assert.True(Restart(session, T0));
        Assert.True(Restart(session, T0.AddSeconds(6)));
        Assert.True(Restart(session, T0.AddSeconds(12)));

        // The third replacement ran for eleven minutes before dying: a success, so the budget starts over.
        var later = T0.AddSeconds(12) + Healthy + TimeSpan.FromMinutes(1);
        Ended(session, later);
        Assert.True(Restart(session, later));
        Assert.Equal(1, session.Restarts);
        Assert.True(Restart(session, later.AddSeconds(6)));
        Assert.True(Restart(session, later.AddSeconds(12)));
        Assert.False(Restart(session, later.AddSeconds(18)));
    }

    [Fact]
    public void ATornDownSession_IsNeverRestarted()
    {
        var session = DeadSession();
        Assert.True(session.MarkTornDown());
        Assert.False(Restart(session, T0));
    }
}
