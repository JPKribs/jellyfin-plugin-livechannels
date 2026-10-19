using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the re-tune vs linger-teardown race: a viewer surfing back adopts the warm session at the same
/// moment its linger grace expires. The adoption and the teardown claim must be mutually exclusive, or the viewer
/// is handed a session whose encoder is cancelled and directory deleted under them.
/// </summary>
public class AdoptTeardownRaceTests
{
    private static LiveChannelsTvService.LiveSession NewSession()
        => new("lc_test", new CancellationTokenSource(), Task.Delay(Timeout.Infinite), "/tmp/lc_test", "Test", "chan", 1, DateTime.UtcNow, new SessionStats());

    [Fact]
    public void TornDownSession_RefusesANewConsumer()
    {
        var session = NewSession();
        Assert.True(session.MarkTornDown());
        Assert.False(session.AddConsumer("late"));
        Assert.False(session.HasConsumers);
    }

    [Fact]
    public void IdleTeardown_IsRefused_OnceAViewerAdopted()
    {
        var session = NewSession();
        session.AddConsumer("first");
        session.RemoveConsumer("first");

        // The linger saw the session empty, then a re-tune adopted it before the teardown claim.
        Assert.True(session.AddConsumer("retune"));
        Assert.False(session.MarkTornDown(onlyIfIdle: true));
        Assert.False(session.IsTornDown);

        // An explicit kill still wins.
        Assert.True(session.MarkTornDown());
    }

    [Fact]
    public async Task ConcurrentAdoptAndLingerTeardown_NeverBothWin()
    {
        // Hammer the exact interleaving: one thread adopts, one claims an idle teardown, released together.
        // Exactly one may succeed every time; both succeeding is the bug (an adopted viewer on a dead session).
        for (var round = 0; round < 2000; round++)
        {
            var session = NewSession();
            session.AddConsumer("first");
            session.RemoveConsumer("first");

            using var go = new ManualResetEventSlim(false);
            var adopted = false;
            var tornDown = false;
            var adopt = Task.Run(() =>
            {
                go.Wait();
                adopted = session.AddConsumer("retune");
            });
            var teardown = Task.Run(() =>
            {
                go.Wait();
                tornDown = session.MarkTornDown(onlyIfIdle: true);
            });

            go.Set();
            await Task.WhenAll(adopt, teardown);

            Assert.True(adopted ^ tornDown, $"round {round}: adopted={adopted}, tornDown={tornDown}");
        }
    }

    [Fact]
    public void RunGuarded_LogsInsteadOfThrowing()
    {
        // The reaper runs from a Timer callback, where an escaping exception terminates the server process.
        var logger = Substitute.For<ILogger>();
        var ran = LiveChannelsTvService.RunGuarded(() => throw new InvalidOperationException("boom"), logger, "the test sweep");

        Assert.False(ran);
        logger.ReceivedWithAnyArgs(1).Log(LogLevel.Error, default, default(object)!, null, null!);
        Assert.True(LiveChannelsTvService.RunGuarded(() => { }, logger, "the test sweep"));
    }
}
