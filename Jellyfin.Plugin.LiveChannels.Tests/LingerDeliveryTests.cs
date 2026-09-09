using System;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the delivery half of the "is anyone watching" rule: <see cref="LiveChannelsTvService.DeliveryCountsAsWatched"/>.
/// A lingering session must not be kept alive by the bytes its departed viewer was served before closing, or
/// the 30s countdown is vetoed every time and the encoder runs on until the reaper's backstop.
/// </summary>
public class LingerDeliveryTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverServed_IsNotWatched()
    {
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(null, null, Now));
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(null, Now.AddSeconds(-30), Now));
    }

    [Fact]
    public void AttachedViewer_RecentDeliveryCounts_OldDeliveryDoesNot()
    {
        Assert.True(LiveChannelsTvService.DeliveryCountsAsWatched(Now.AddSeconds(-90), null, Now));
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(Now.AddMinutes(-3), null, Now));
    }

    [Fact]
    public void Lingering_DeliveryBeforeTheClose_DoesNotCount()
    {
        // The log's exact shape: reader closed at T-2s, Jellyfin closed the stream at T, the linger fires at T+30s.
        var close = Now.AddSeconds(-30);
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(close.AddSeconds(-2), close, Now));
    }

    [Fact]
    public void Lingering_ReaderDrainingRightAfterTheClose_DoesNotCount()
    {
        var close = Now.AddSeconds(-30);
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(close.AddSeconds(2), close, Now));
    }

    [Fact]
    public void Lingering_SomeoneStillPullingAfterTheClose_Counts_UntilTheyGoQuiet()
    {
        var close = Now.AddSeconds(-30);

        // Bytes went out 10s ago, well after the close: a reader is still on the other end.
        Assert.True(LiveChannelsTvService.DeliveryCountsAsWatched(Now.AddSeconds(-10), close, Now));

        // The same reader, quiet for longer than the grace: collect the session.
        Assert.False(LiveChannelsTvService.DeliveryCountsAsWatched(close.AddSeconds(10), close, close.AddSeconds(45)));
    }
}
