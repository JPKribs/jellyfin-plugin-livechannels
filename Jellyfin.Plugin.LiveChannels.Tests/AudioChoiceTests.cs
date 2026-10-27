using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// A channel plays, for each item, the default track in its preferred language, else the first track in that
/// language, else the default track, else the first track.
/// </summary>
public class AudioChoiceTests
{
    private static List<MediaStream> Tracks(params (string? Language, bool IsDefault)[] audio)
    {
        var streams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } };
        for (var i = 0; i < audio.Length; i++)
        {
            streams.Add(new MediaStream { Type = MediaStreamType.Audio, Index = i + 1, Language = audio[i].Language, IsDefault = audio[i].IsDefault });
        }

        streams.Add(new MediaStream { Type = MediaStreamType.Subtitle, Index = audio.Length + 1, Language = "eng", IsDefault = true });
        return streams;
    }

    private static int? Choose(List<MediaStream> streams, string? language) => ChannelService.ChooseAudio(streams, language)?.Ordinal;

    [Fact]
    public void DefaultTrackInTheLanguage_WinsOverAnEarlierOne()
        => Assert.Equal(2, Choose(Tracks(("ger", false), ("eng", false), ("eng", true)), "eng"));

    [Fact]
    public void FirstTrackInTheLanguage_WinsOverTheDefaultTrack()
        => Assert.Equal(1, Choose(Tracks(("fre", true), ("eng", false), ("eng", false)), "eng"));

    [Fact]
    public void NoTrackInTheLanguage_FallsBackToTheDefault()
        => Assert.Equal(1, Choose(Tracks(("fre", false), ("ger", true)), "eng"));

    [Fact]
    public void NoTrackInTheLanguage_AndNoDefault_FallsBackToTheFirst()
        => Assert.Equal(0, Choose(Tracks(("fre", false), ("ger", false)), "eng"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPreferredLanguage_PlaysTheDefault_ElseTheFirst(string? language)
    {
        Assert.Equal(1, Choose(Tracks(("fre", false), ("ger", true)), language));
        Assert.Equal(0, Choose(Tracks(("fre", false), ("ger", false)), language));
    }

    [Fact]
    public void UntaggedTracks_AreNeverTakenForTheLanguage()
        => Assert.Equal(2, Choose(Tracks((null, false), ("und", false), ("eng", false)), "eng"));

    [Fact]
    public void NoAudio_ChoosesNothing()
        => Assert.Null(Choose(new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0 } }, "eng"));

    [Theory]
    [InlineData("eng", "en", true)]
    [InlineData("EN", "eng", true)]
    [InlineData("ger", "deu", true)] // bibliographic and terminology codes
    [InlineData("de", "ger", true)]
    [InlineData("fre", "fra", true)]
    [InlineData("chi", "zho", true)]
    [InlineData("en-US", "eng", true)]
    [InlineData("eng", "fre", false)]
    [InlineData("", "eng", false)]
    [InlineData(null, null, false)]
    public void Languages_MatchAcrossCodeForms(string? a, string? b, bool expected)
        => Assert.Equal(expected, ChannelService.LanguageMatches(a, b));

    [Fact]
    public void ALanguageWrittenAsATwoLetterCode_StillFindsItsTrack()
        => Assert.Equal(1, Choose(Tracks(("fre", true), ("ger", false)), "de"));
}
