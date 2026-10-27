using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="ChannelService.FindBurnInSubtitle"/>: which track a channel burns in, and which tracks
/// are not burnable at all.
/// </summary>
public class BurnInSubtitleTests
{
    private static ProgramEntry Program(params SubtitleStreamInfo[] subtitles)
        => new(Guid.NewGuid(), "Item", null, 10_000_000L, "/media/item.mkv") { Subtitles = subtitles };

    private static SubtitleStreamInfo Sub(int index, bool text = true, bool forced = false, bool isDefault = false, bool external = false)
        => new() { RelativeIndex = index, AbsoluteIndex = index + 2, IsText = text, IsForced = forced, IsDefault = isDefault, IsExternal = external };

    [Fact]
    public void Never_BurnsNothing()
        => Assert.Null(ChannelService.FindBurnInSubtitle(Program(Sub(0, forced: true)), SubtitleBurnInMode.Never));

    [Fact]
    public void Forced_BurnsTheForcedTrack()
    {
        var chosen = ChannelService.FindBurnInSubtitle(Program(Sub(0), Sub(1, forced: true)), SubtitleBurnInMode.Forced);

        Assert.NotNull(chosen);
        Assert.Equal(1, chosen!.Value.RelativeIndex);
    }

    [Fact]
    public void Forced_WithNoForcedTrackAndNoLanguageMismatch_BurnsNothing()
        => Assert.Null(ChannelService.FindBurnInSubtitle(Program(Sub(0), Sub(1)), SubtitleBurnInMode.Forced));

    [Fact]
    public void Always_PrefersTheDefaultTrack()
    {
        var chosen = ChannelService.FindBurnInSubtitle(Program(Sub(0), Sub(1, isDefault: true)), SubtitleBurnInMode.Always);

        Assert.NotNull(chosen);
        Assert.Equal(1, chosen!.Value.RelativeIndex);
    }

    [Fact]
    public void Always_FallsBackToTheFirstTrack()
    {
        var chosen = ChannelService.FindBurnInSubtitle(Program(Sub(3), Sub(4)), SubtitleBurnInMode.Always);

        Assert.NotNull(chosen);
        Assert.Equal(3, chosen!.Value.RelativeIndex);
    }

    [Fact]
    public void ExternalBitmapTrack_IsSkipped_SoABurnableOneIsChosenInstead()
    {
        // An external .sup is a picture with no stream inside the media file to composite from, so it can never
        // be burned; the embedded text track is used instead of the channel showing nothing.
        var chosen = ChannelService.FindBurnInSubtitle(
            Program(Sub(0, text: false, external: true, isDefault: true), Sub(1)),
            SubtitleBurnInMode.Always);

        Assert.NotNull(chosen);
        Assert.Equal(1, chosen!.Value.RelativeIndex);
        Assert.True(chosen.Value.IsText);
    }

    [Fact]
    public void ExternalTextTrack_IsStillBurnable()
    {
        var chosen = ChannelService.FindBurnInSubtitle(Program(Sub(0, external: true)), SubtitleBurnInMode.Always);

        Assert.NotNull(chosen);
        Assert.Equal(0, chosen!.Value.RelativeIndex);
    }

    [Fact]
    public void OnlyAnExternalBitmapTrack_BurnsNothing()
        => Assert.Null(ChannelService.FindBurnInSubtitle(Program(Sub(0, text: false, external: true, forced: true)), SubtitleBurnInMode.Always));

    // ---- Languages ----

    private static ProgramEntry Spoken(string? audio, params SubtitleStreamInfo[] subtitles)
        => new(Guid.NewGuid(), "Item", null, 10_000_000L, "/media/item.mkv") { Subtitles = subtitles, DefaultAudioLanguage = audio };

    private static SubtitleStreamInfo Lang(int index, string? language, bool forced = false, bool isDefault = false)
        => new() { RelativeIndex = index, AbsoluteIndex = index + 2, IsText = true, IsForced = forced, IsDefault = isDefault, Language = language };

    [Theory]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("mul")]
    [InlineData("")]
    [InlineData(null)]
    public void ForcedOnly_UntaggedAudio_BurnsNothing(string? audio)
        => Assert.Null(ChannelService.FindBurnInSubtitle(Spoken(audio, Lang(0, "eng", isDefault: true)), SubtitleBurnInMode.Forced, "eng"));

    [Fact]
    public void ForcedOnly_AudioInTheViewersLanguage_BurnsNothing()
        => Assert.Null(ChannelService.FindBurnInSubtitle(Spoken("en", Lang(0, "eng", isDefault: true)), SubtitleBurnInMode.Forced, "eng"));

    [Fact]
    public void ForcedOnly_ForeignAudio_BurnsTheViewersLanguage_NotTheDefaultTrack()
    {
        // The default subtitle is the Japanese one; the English one is what an English viewer can read.
        var chosen = ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn", isDefault: true), Lang(1, "eng"), Lang(2, "eng", isDefault: true)), SubtitleBurnInMode.Forced, "eng");
        Assert.Equal(2, chosen?.RelativeIndex);
    }

    [Fact]
    public void ForcedOnly_ForeignAudio_WithNoReadableTrack_BurnsNothing()
        => Assert.Null(ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn", isDefault: true), Lang(1, "fre")), SubtitleBurnInMode.Forced, "eng"));

    [Fact]
    public void ForcedOnly_ForeignAudio_FallsBackToAnUntaggedTrack()
        => Assert.Equal(1, ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn", isDefault: true), Lang(1, null)), SubtitleBurnInMode.Forced, "eng")?.RelativeIndex);

    [Fact]
    public void AForcedTrackInAnotherLanguage_IsNotBurnedForTheViewer()
    {
        // Japanese audio with a forced Japanese sign track: no use to an English viewer, who gets the English track.
        var chosen = ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn", forced: true), Lang(1, "eng")), SubtitleBurnInMode.Forced, "eng");
        Assert.Equal(1, chosen?.RelativeIndex);
    }

    [Fact]
    public void AForcedTrackInTheViewersLanguage_WinsOverAnEarlierForcedOne()
        => Assert.Equal(1, ChannelService.FindBurnInSubtitle(Spoken("eng", Lang(0, "fre", forced: true), Lang(1, "eng", forced: true)), SubtitleBurnInMode.Forced, "eng")?.RelativeIndex);

    [Fact]
    public void Always_PrefersTheViewersLanguage_ThenBurnsWhateverThereIs()
    {
        Assert.Equal(1, ChannelService.FindBurnInSubtitle(Spoken("eng", Lang(0, "fre", isDefault: true), Lang(1, "eng")), SubtitleBurnInMode.Always, "eng")?.RelativeIndex);
        Assert.Equal(0, ChannelService.FindBurnInSubtitle(Spoken("eng", Lang(0, "fre", isDefault: true), Lang(1, "ger")), SubtitleBurnInMode.Always, "eng")?.RelativeIndex);
    }

    [Fact]
    public void WithNoLanguageConfigured_TheOldChoiceStands()
    {
        Assert.Equal(0, ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn", forced: true), Lang(1, "eng")), SubtitleBurnInMode.Forced, string.Empty)?.RelativeIndex);
        Assert.Null(ChannelService.FindBurnInSubtitle(Spoken("jpn", Lang(0, "jpn")), SubtitleBurnInMode.Forced, string.Empty));
    }
}
