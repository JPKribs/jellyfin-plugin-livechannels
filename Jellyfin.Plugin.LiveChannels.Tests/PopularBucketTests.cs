using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the Popular channel's bucket selection: <see cref="ChannelService.SelectBuckets"/> fills ordered
/// buckets up to their quotas while de-duplicating across buckets and backfilling.
/// </summary>
public class PopularBucketTests
{
    [Fact]
    public void TakesUpToQuota_PerBucket()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        var result = ChannelService.SelectBuckets(
            (new List<Guid> { a, b, c }, 2),
            (new List<Guid> { d }, 2));

        // Two from the first bucket (quota), one from the second (only one available).
        Assert.Equal(new[] { a, b, d }, result);
    }

    [Fact]
    public void DedupesAcrossBuckets_AndBackfills()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        // The second bucket's first candidate (a) is already taken, so it backfills with c.
        var result = ChannelService.SelectBuckets(
            (new List<Guid> { a, b }, 2),
            (new List<Guid> { a, c }, 1));

        Assert.Equal(new[] { a, b, c }, result);
    }

    [Fact]
    public void ShortBucket_ReturnsFewer()
    {
        var a = Guid.NewGuid();
        Assert.Single(ChannelService.SelectBuckets((new List<Guid> { a }, 5)));
    }

    [Fact]
    public void EmptyBucket_ContributesNothing()
    {
        var a = Guid.NewGuid();

        var result = ChannelService.SelectBuckets(
            (new List<Guid>(), 3),
            (new List<Guid> { a }, 3));

        Assert.Equal(new[] { a }, result);
    }
}
