using System;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="RatingSchedule"/>: the effective-window overlap rule, custom/wrap-around windows, and
/// the transition buffer that keeps content compliant as it bleeds across a daypart boundary.
/// </summary>
public class RatingScheduleTests
{
    // Sample parental scores used across the tests.
    private const int G = 0;
    private const int Pg = 100;
    private const int Pg13 = 200;
    private const int R = 400;

    // MARK: RatingWindow.Allows

    [Fact]
    public void Allows_RespectsBoundsAndUnratedRule()
    {
        var window = new RatingWindow(Pg, R, IncludeUnrated: false);

        Assert.False(window.Allows(G));      // below the floor
        Assert.True(window.Allows(Pg));      // on the floor
        Assert.True(window.Allows(Pg13));    // inside
        Assert.True(window.Allows(R));       // on the cap
        Assert.False(window.Allows(500));    // above the cap
        Assert.False(window.Allows(null));   // unrated, and unrated not allowed
    }

    [Fact]
    public void Allows_UnrestrictedAllowsEverything()
    {
        Assert.True(RatingWindow.Unrestricted.Allows(null));
        Assert.True(RatingWindow.Unrestricted.Allows(int.MaxValue));
        Assert.True(RatingWindow.Unrestricted.Allows(int.MinValue));
    }

    [Fact]
    public void Allows_UnratedAllowedIgnoresBounds()
    {
        var window = new RatingWindow(Pg, R, IncludeUnrated: true);
        Assert.True(window.Allows(null));
    }

    // MARK: RatingWindow.Combine -- lowest min, lowest max, unrated only if both allow

    [Fact]
    public void Combine_TakesLowestMinAndLowestMax()
    {
        var combined = new RatingWindow(Pg, R, IncludeUnrated: true)
            .Combine(new RatingWindow(G, Pg13, IncludeUnrated: false));

        Assert.Equal(G, combined.Min);          // lowest min
        Assert.Equal(Pg13, combined.Max);       // lowest max
        Assert.False(combined.IncludeUnrated);  // both must allow
    }

    [Fact]
    public void Combine_UnrestrictedDropsFloorAndKeepsOtherCap()
    {
        var combined = RatingWindow.Unrestricted.Combine(new RatingWindow(Pg, R, IncludeUnrated: true));

        Assert.Null(combined.Min);   // no floor is the lowest min
        Assert.Equal(R, combined.Max);
        Assert.True(combined.IncludeUnrated);
    }

    // MARK: EffectiveWindow

    [Fact]
    public void EffectiveWindow_NoBlocksIsUnrestricted()
    {
        Assert.Equal(RatingWindow.Unrestricted, RatingSchedule.EffectiveWindow(Array.Empty<ResolvedRatingBlock>(), 600));
    }

    [Fact]
    public void EffectiveWindow_UncoveredTimeIsUnrestricted()
    {
        // A single custom block 20:00-23:00; 10:00 is not covered by any block.
        var blocks = new[] { Custom(new RatingWindow(null, Pg, true), 20 * 60, 23 * 60) };
        Assert.Equal(RatingWindow.Unrestricted, RatingSchedule.EffectiveWindow(blocks, 10 * 60));
        Assert.Equal(new RatingWindow(null, Pg, true), RatingSchedule.EffectiveWindow(blocks, 21 * 60));
    }

    [Fact]
    public void EffectiveWindow_OverlappingBlocksCombine()
    {
        var blocks = new[]
        {
            AllDay(new RatingWindow(null, R, true)),
            AllDay(new RatingWindow(Pg, Pg13, false))
        };

        var window = RatingSchedule.EffectiveWindow(blocks, 720);
        Assert.Null(window.Min);           // lowest min (no floor vs Pg -> no floor)
        Assert.Equal(Pg13, window.Max);    // lowest max (R vs Pg13 -> Pg13)
        Assert.False(window.IncludeUnrated);
    }

    [Fact]
    public void EffectiveWindow_WrapAroundBlockSpansMidnight()
    {
        // 22:00 -> 04:00 wraps past midnight.
        var blocks = new[] { Custom(new RatingWindow(null, R, true), 22 * 60, 4 * 60) };

        Assert.True(RatingSchedule.EffectiveWindow(blocks, 23 * 60).Max == R);  // 23:00 active
        Assert.True(RatingSchedule.EffectiveWindow(blocks, 2 * 60).Max == R);   // 02:00 active
        Assert.Equal(RatingWindow.Unrestricted, RatingSchedule.EffectiveWindow(blocks, 12 * 60)); // noon inactive
    }

    // MARK: WindowForStart -- the transition buffer (the PG/R worked example)

    // Daytime 06:00-20:00 caps at PG; the rest of the day (20:00-06:00) caps at R.
    private static ResolvedRatingBlock[] PgDayRNightBlocks() => new[]
    {
        Custom(new RatingWindow(null, Pg, true), 6 * 60, 20 * 60),   // 06:00-20:00 up to PG
        Custom(new RatingWindow(null, R, true), 20 * 60, 6 * 60)     // 20:00-06:00 (wrap) up to R
    };

    [Fact]
    public void WindowForStart_NoTransitionUsesTheStartWindow()
    {
        var blocks = PgDayRNightBlocks();
        Assert.Equal(Pg, RatingSchedule.WindowForStart(blocks, 18 * 60, 0).Max); // 18:00, no buffer -> PG
        Assert.Equal(R, RatingSchedule.WindowForStart(blocks, 0, 0).Max);        // 00:00, no buffer -> R
    }

    [Fact]
    public void WindowForStart_ApproachingStricterWindowCapsAtStricter()
    {
        var blocks = PgDayRNightBlocks();
        // 04:00 is in the R window, but within 2h of the 06:00 PG boundary -> capped at PG.
        Assert.Equal(Pg, RatingSchedule.WindowForStart(blocks, 4 * 60, 120).Max);
    }

    [Fact]
    public void WindowForStart_ApproachingLooserWindowStillCapsAtStricter()
    {
        var blocks = PgDayRNightBlocks();
        // 18:00 is in the PG window, within 2h of the 20:00 R boundary -> still PG (lowest max of PG and R).
        Assert.Equal(Pg, RatingSchedule.WindowForStart(blocks, 18 * 60, 120).Max);
    }

    [Fact]
    public void WindowForStart_AwayFromBoundaryUsesTheLocalWindow()
    {
        var blocks = PgDayRNightBlocks();
        Assert.Equal(R, RatingSchedule.WindowForStart(blocks, 0, 120).Max);        // 00:00, no boundary in [00:00,02:00] -> R
        Assert.Equal(Pg, RatingSchedule.WindowForStart(blocks, 12 * 60, 120).Max); // 12:00, no boundary in [12:00,14:00] -> PG
    }

    // MARK: AllowedByAnyWindow -- the union used to build a capped population (e.g. the Popular channel)

    [Fact]
    public void AllowedByAnyWindow_NoBlocksAllowsEverything()
        => Assert.True(RatingSchedule.AllowedByAnyWindow(Array.Empty<ResolvedRatingBlock>(), R));

    [Fact]
    public void AllowedByAnyWindow_SingleAllDayBandIsStrict()
    {
        var blocks = new[] { AllDay(new RatingWindow(null, Pg, true)) };
        Assert.True(RatingSchedule.AllowedByAnyWindow(blocks, Pg));   // within the band
        Assert.False(RatingSchedule.AllowedByAnyWindow(blocks, R));   // above the cap, no window admits it
    }

    [Fact]
    public void AllowedByAnyWindow_TimeOfDayIsTheUnionAcrossWindows()
    {
        // Daytime up to PG, night up to R. An R item is admitted (the night window), a PG item always.
        var blocks = PgDayRNightBlocks();
        Assert.True(RatingSchedule.AllowedByAnyWindow(blocks, R));    // valid for the night window
        Assert.True(RatingSchedule.AllowedByAnyWindow(blocks, Pg));   // valid for both
    }

    private static ResolvedRatingBlock AllDay(RatingWindow window)
        => new(window, AllDay: true, StartMinutes: 0, EndMinutes: 0);

    private static ResolvedRatingBlock Custom(RatingWindow window, int startMinutes, int endMinutes)
        => new(window, AllDay: false, startMinutes, endMinutes);
}
