using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// A series rule records the series it was created for, whatever a later schedule rebuild puts into the past slot
/// its anchor program id names: the series is stamped on the rule, kept across client edits, and an older,
/// unstamped rule is matched by its name rather than by whatever now fills that slot.
/// </summary>
public sealed class SeriesRuleKeyTests : IDisposable
{
    private static readonly Guid ShowX = Guid.NewGuid();
    private static readonly Guid ShowY = Guid.NewGuid();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "livechannels-rulekey-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IApplicationPaths _paths;

    public SeriesRuleKeyTests()
    {
        _paths = Substitute.For<IApplicationPaths>();
        _paths.DataPath.Returns(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing persisted.
        }
    }

    private static ProgramEntry Episode(Guid series, string seriesName, string episode)
        => new(Guid.NewGuid(), episode, null, TimeSpan.FromMinutes(30).Ticks, "/media/" + episode + ".mkv") { SeriesId = series, SeriesName = seriesName };

    private static ProgramEntry Movie(string title)
        => new(Guid.NewGuid(), title, null, TimeSpan.FromHours(2).Ticks, "/media/" + title + ".mkv");

    private TimerService NewService() => new(_paths, NullLogger<TimerService>.Instance);

    [Fact]
    public void AStampedRule_IsKeyedByItsStamp()
    {
        var rule = new SeriesTimerInfo { SeriesId = ShowX.ToString("N") };
        Assert.Equal(ShowX, RecordingService.RuleKey(rule));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000000")]
    public void ARuleWithoutAUsableStamp_HasNoKey(string? seriesId)
        => Assert.Null(RecordingService.RuleKey(new SeriesTimerInfo { SeriesId = seriesId }));

    [Fact]
    public void KeyOf_IsTheSeriesForAnEpisode_AndTheItemForAMovie()
    {
        var episode = Episode(ShowX, "Show X", "Pilot");
        var movie = Movie("Heat");
        Assert.Equal(ShowX, RecordingService.KeyOf(episode));
        Assert.Equal(movie.ItemId, RecordingService.KeyOf(movie));
    }

    [Fact]
    public void LegacyRule_WhoseAnchorStillAirsTheShow_KeepsIt()
    {
        var x = Episode(ShowX, "Show X", "Pilot");
        Assert.Equal(ShowX, RecordingService.LegacyRuleKey("Show X", x, new[] { x }));
    }

    [Fact]
    public void LegacyRule_WhoseAnchorNowAirsAnotherShow_FindsItsOwnByName()
    {
        // The rule was created on Show X; a rebuild since put Show Y into that past slot. The old code switched
        // the rule to Show Y (and could then merge it into a Show Y rule, deleting the user's Show X rule).
        var x = Episode(ShowX, "Show X", "Pilot");
        var y = Episode(ShowY, "Show Y", "Opener");
        Assert.Equal(ShowX, RecordingService.LegacyRuleKey("Show X", y, new[] { y, x }));
    }

    [Fact]
    public void LegacyRule_ForAMovie_MatchesTheMovieTitle()
    {
        var heat = Movie("Heat");
        var y = Episode(ShowY, "Show Y", "Opener");
        Assert.Equal(heat.ItemId, RecordingService.LegacyRuleKey("heat", y, new[] { y, heat }));
    }

    [Fact]
    public void LegacyRule_WithAnAmbiguousOrMissingName_IsLeftAlone()
    {
        var a = Movie("Twins");
        var b = Movie("Twins");
        var y = Episode(ShowY, "Show Y", "Opener");
        Assert.Null(RecordingService.LegacyRuleKey("Twins", y, new[] { a, b, y }));
        Assert.Null(RecordingService.LegacyRuleKey("Gone", y, new[] { y }));
        Assert.Null(RecordingService.LegacyRuleKey(null, y, new[] { y }));
    }

    [Fact]
    public void AClientEdit_KeepsTheRulesStamp()
    {
        var service = NewService();
        var id = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show X", ProgramId = "chan_1", SeriesId = ShowX.ToString("N") });

        // Clients send the edited rule back without the stamp.
        service.SaveSeriesTimer(new SeriesTimerInfo { Id = id, Name = "Show X", ProgramId = "chan_1", PrePaddingSeconds = 60 });

        var reloaded = Assert.Single(NewService().GetSeriesTimers());
        Assert.Equal(ShowX, RecordingService.RuleKey(reloaded));
        Assert.Equal(60, reloaded.PrePaddingSeconds);
    }

    [Fact]
    public void SetSeriesKey_StampsAStoredRule_AcrossReload()
    {
        var service = NewService();
        var id = service.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show X", ProgramId = "chan_1" });
        service.SetSeriesKey(id, ShowX.ToString("N"));
        Assert.Equal(ShowX, RecordingService.RuleKey(Assert.Single(NewService().GetSeriesTimers())));
    }
}
