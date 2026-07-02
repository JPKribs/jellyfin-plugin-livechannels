using System;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the stress test's pure logic: the recommendation derived from completed rounds.
/// </summary>
public class StressTestTests
{
    [Fact]
    public void Recommend_IsTheHighestFullyPassingRound()
        => Assert.Equal(3, StressTestService.Recommend(new[]
        {
            new StressRound(1, 34.1, true),
            new StressRound(2, 31.0, true),
            new StressRound(3, 29.4, true),
            new StressRound(4, 21.7, false)
        }));

    [Fact]
    public void Recommend_IsZero_WhenEvenOneStreamCannotKeepUp()
        => Assert.Equal(0, StressTestService.Recommend(new[] { new StressRound(1, 14.2, false) }));

    [Fact]
    public void Recommend_IsZero_WithNoRounds()
        => Assert.Equal(0, StressTestService.Recommend(Array.Empty<StressRound>()));
}
