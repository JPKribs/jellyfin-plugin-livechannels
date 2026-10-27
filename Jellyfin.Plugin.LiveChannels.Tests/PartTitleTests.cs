using System;
using System.Diagnostics;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Multi-part episode titles split into their shared base and part number, in time linear in the title's length.
/// </summary>
public class PartTitleTests
{
    [Theory]
    [InlineData("The Trap (2)", "The Trap", 2)]
    [InlineData("The Trap [2]", "The Trap", 2)]
    [InlineData("The Trap - Part 2", "The Trap", 2)]
    [InlineData("The Trap: Pt. 2", "The Trap", 2)]
    [InlineData("The Trap Pt 12", "The Trap", 12)]
    [InlineData("the trap PART 3  ", "the trap", 3)]
    [InlineData("Act (1) of the Play (3)", "Act (1) of the Play", 3)]
    public void PartMarkers_SplitIntoBaseAndNumber(string title, string expectedBase, int expectedPart)
    {
        Assert.True(ProgramLoopBuilder.TryParsePart(title, out var baseTitle, out var part));
        Assert.Equal(expectedBase, baseTitle);
        Assert.Equal(expectedPart, part);
    }

    [Theory]
    [InlineData("Plain Title")]
    [InlineData("(2)")]
    [InlineData("Episode 200")]
    [InlineData("")]
    [InlineData(null)]
    public void TitlesWithoutAPartMarker_DoNotSplit(string? title)
        => Assert.False(ProgramLoopBuilder.TryParsePart(title, out _, out _));

    [Fact]
    public void AHostileTitle_IsParsedQuickly()
    {
        var title = new string(' ', 50_000) + "x(1";
        var watch = Stopwatch.StartNew();
        ProgramLoopBuilder.TryParsePart(title, out _, out _);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "took " + watch.ElapsedMilliseconds + " ms");
    }
}
