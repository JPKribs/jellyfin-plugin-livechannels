using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="ConfigurationValidator"/> — the server-side guard against bad config from any API
/// client (duplicate channel numbers, malformed or oversized logos, out-of-range numerics, and a stream
/// directory that would point the destructive sweeps at existing data).
/// </summary>
public class ConfigurationValidatorTests
{
    private static PluginConfiguration WithChannels(params Channel[] channels)
    {
        var config = new PluginConfiguration();
        config.Channels.AddRange(channels);
        return config;
    }

    private static Channel Ch(int number, bool enabled = true)
        => new()
        {
            Id = "id" + number,
            Name = "Ch" + number,
            Number = number,
            Enabled = enabled,
            Sources = { new LibrarySource { LibraryId = "lib" } }
        };

    [Fact]
    public void ValidConfig_DoesNotThrow()
        => ConfigurationValidator.Validate(WithChannels(Ch(1), Ch(2), Ch(3)));

    [Fact]
    public void DuplicateEnabledNumber_Throws()
        => Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(Ch(5), Ch(5))));

    [Fact]
    public void DuplicateNumber_AllowedWhenOneIsDisabled()
        => ConfigurationValidator.Validate(WithChannels(Ch(5), Ch(5, enabled: false)));

    [Fact]
    public void EnabledChannelWithoutNumber_Throws()
        => Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(Ch(0))));

    [Fact]
    public void EnabledChannelWithoutSources_Throws()
    {
        var channel = Ch(1);
        channel.Sources.Clear();
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void DisabledChannel_IsExemptFromServingRules()
    {
        var draft = new Channel { Id = "d", Name = "Draft", Number = 0, Enabled = false };
        ConfigurationValidator.Validate(WithChannels(draft)); // no number, no sources, but disabled
    }

    [Fact]
    public void InvalidBase64Logo_Throws()
    {
        var channel = Ch(1);
        channel.LogoData = "not valid base64 @@@";
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void OversizedLogo_Throws()
    {
        var channel = Ch(1);
        channel.LogoData = Convert.ToBase64String(new byte[ConfigurationValidator.MaxLogoBytes + 1]);
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void APngLogo_IsAccepted_AndTypedFromItsBytes()
    {
        var channel = Ch(1);
        channel.LogoData = Convert.ToBase64String(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 });
        channel.LogoContentType = "image/jpeg"; // wrong: the bytes decide
        ConfigurationValidator.Validate(WithChannels(channel));
        Assert.Equal("image/png", channel.LogoContentType);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    public void OtherImageLogos_AreTypedFromTheirBytes(byte[] bytes, string type)
        => Assert.Equal(type, ConfigurationValidator.ImageContentType(bytes));

    [Theory]
    [InlineData("#EXTM3U\nhttp://example.com/x.ts\n")] // a playlist ffmpeg would fetch
    [InlineData("ffconcat version 1.0\nfile '/etc/passwd'\n")]
    [InlineData("\u0001\u0002\u0003\u0004")]
    public void ALogoThatIsNotAnImage_IsRefused(string content)
    {
        var channel = Ch(1);
        channel.LogoData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void AnOlderLogoOfAnotherType_DoesNotBlockAnUnrelatedSave()
    {
        // A BMP logo saved by an earlier version stays as it is; only a newly set logo must be a known image.
        var bmp = Convert.ToBase64String(new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0 });
        var stored = WithChannels(Ch(1));
        stored.Channels[0].LogoData = bmp;
        var incoming = WithChannels(Ch(1));
        incoming.Channels[0].LogoData = bmp;
        incoming.TranscodeVideoBitrateKbps = 6000;
        ConfigurationValidator.Validate(incoming, stored);

        var replaced = WithChannels(Ch(1));
        replaced.Channels[0].LogoData = Convert.ToBase64String(new byte[] { (byte)'B', (byte)'M', 1, 1, 1, 1 });
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(replaced, stored));
    }

    [Fact]
    public void CustomRatingBlockWithEqualTimes_Throws()
    {
        var channel = Ch(1);
        channel.RatingBlocks.Add(new RatingBlock { Period = RatingBlockPeriod.Custom, StartMinutes = 600, EndMinutes = 600 });
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void CustomRatingBlockOutOfRange_Throws()
    {
        var channel = Ch(1);
        channel.RatingBlocks.Add(new RatingBlock { Period = RatingBlockPeriod.Custom, StartMinutes = 0, EndMinutes = 1500 });
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }

    [Fact]
    public void ValidCustomAndAllDayBlocks_DoNotThrow()
    {
        var channel = Ch(1);
        channel.RatingBlocks.Add(new RatingBlock { Period = RatingBlockPeriod.AllDay });
        channel.RatingBlocks.Add(new RatingBlock { Period = RatingBlockPeriod.Custom, StartMinutes = 22 * 60, EndMinutes = 4 * 60 }); // wrap
        ConfigurationValidator.Validate(WithChannels(channel));
    }

    [Fact]
    public void EffectiveRatingBlocks_MigratesLegacyBandAndDropUnrated()
    {
        var legacy = new Channel { MinOfficialRating = "PG", MaxOfficialRating = "TV-14", IncludeUnrated = false };
        var blocks = legacy.EffectiveRatingBlocks();
        Assert.Single(blocks);
        Assert.Equal("PG", blocks[0].MinOfficialRating);
        Assert.Equal("TV-14", blocks[0].MaxOfficialRating);
        Assert.False(blocks[0].IncludeUnrated);
        Assert.Equal(RatingBlockPeriod.AllDay, blocks[0].Period);

        Assert.Empty(new Channel().EffectiveRatingBlocks()); // all defaults -> no restriction
    }

    [Fact]
    public void SubtitleColour_MustBeHex()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { SubtitleTextColor = "yellow" }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { SubtitleOutlineColor = "#12345" }));

        // Blank means "leave the subtitle's own colour alone", which is the default.
        ConfigurationValidator.Validate(new PluginConfiguration { SubtitleTextColor = string.Empty, SubtitleOutlineColor = "#101820" });
    }

    [Fact]
    public void SubtitleSize_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { SubtitleFontScalePercent = 10 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { SubtitleFontScalePercent = 1000 }));
        ConfigurationValidator.Validate(new PluginConfiguration { SubtitleFontScalePercent = 0 }); // saved before styling existed
    }

    [Fact]
    public void TranscodeWidth_OutOfRange_Throws()
    {
        // A zero width builds an ffmpeg scale filter that fails every stream at encode time; it must be
        // rejected at save, where there is still a reason attached.
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { TranscodeWidth = 0 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { TranscodeWidth = -1280 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { TranscodeWidth = 10000 }));
        ConfigurationValidator.Validate(new PluginConfiguration { TranscodeWidth = 3840 });
    }

    [Fact]
    public void VideoBitrate_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { TranscodeVideoBitrateKbps = 0 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { TranscodeVideoBitrateKbps = 500000 }));
    }

    [Fact]
    public void NegativeSessionLimits_Throw()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { MaxConcurrentSessions = -1 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { SessionTimeoutMinutes = -5 }));
        ConfigurationValidator.Validate(new PluginConfiguration { MaxConcurrentSessions = 0, SessionTimeoutMinutes = 0 }); // both mean off
    }

    [Fact]
    public void ChannelNumericLimits_OutOfRange_Throw()
    {
        var transition = Ch(1);
        transition.TransitionWindowMinutes = -1;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(transition)));

        var block = Ch(1);
        block.EpisodesPerBlock = 0;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(block)));

        var community = Ch(1);
        community.MinCommunityRating = 11;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(community)));

        var critic = Ch(1);
        critic.MinCriticRating = -1;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(critic)));
    }

    [Fact]
    public void PopularChannel_IsValidatedToo()
    {
        var badBlock = new PluginConfiguration();
        badBlock.PopularChannel.RatingBlocks.Add(new RatingBlock { Period = RatingBlockPeriod.Custom, StartMinutes = 600, EndMinutes = 600 });
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(badBlock));

        var badNumeric = new PluginConfiguration();
        badNumeric.PopularChannel.EpisodesPerBlock = 0;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(badNumeric));

        // The defaults (and its fixed number 0) stay valid: the enabled-channel number rule must not apply to it.
        ConfigurationValidator.Validate(new PluginConfiguration());
    }

    [Fact]
    public void StreamDirectory_RelativePath_Throws()
        => Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = "streams/live" }));

    [Fact]
    public void StreamDirectory_MissingEmptyOrMarked_IsAccepted()
    {
        // Missing: created (and marked) on first use.
        var missing = Path.Combine(Path.GetTempPath(), "lc-test-" + Guid.NewGuid().ToString("N"));
        ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = missing });

        // Empty: nothing to protect.
        var empty = Directory.CreateTempSubdirectory("lc-test-").FullName;
        try
        {
            ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = empty });
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }

        // Marked: already plugin-owned, whatever else it holds.
        var marked = Directory.CreateTempSubdirectory("lc-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(marked, ChannelService.StreamRootMarkerName), "marker");
            File.WriteAllText(Path.Combine(marked, "leftover.txt"), "x");
            ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = marked });
        }
        finally
        {
            Directory.Delete(marked, recursive: true);
        }
    }

    [Fact]
    public void StreamDirectory_ExistingForeignContent_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("lc-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "family-photos.db"), "precious");
            Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = dir }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StreamDirectory_PreMarkerPluginRoot_IsAccepted()
    {
        // A stream root written by a version before the marker existed: session dirs, the schedule cache, and
        // the legacy single-file schedule are all recognisably ours.
        var dir = Directory.CreateTempSubdirectory("lc-test-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ChannelService.ScheduleDirName));
            Directory.CreateDirectory(Path.Combine(dir, "lc_" + new string('a', 32)));
            File.WriteAllText(Path.Combine(dir, "schedule.json"), "[]");
            ConfigurationValidator.Validate(new PluginConfiguration { StreamDirectory = dir });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TransitionWindow_AboveOneDay_Throws()
    {
        var channel = Ch(1);
        channel.TransitionWindowMinutes = ConfigurationValidator.MaxTransitionWindowMinutes + 1;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));

        channel.TransitionWindowMinutes = ConfigurationValidator.MaxTransitionWindowMinutes;
        ConfigurationValidator.Validate(WithChannels(channel));
    }

    [Fact]
    public void ExplicitNulls_AreReplacedWithDefaults()
    {
        var channel = Ch(1);
        channel.RatingBlocks = null!;
        channel.People = null!;
        channel.Studios = null!;
        channel.Years = null!;
        channel.LogoContentType = null!;
        channel.AudioLanguage = null!;
        channel.Sources[0].Genres = null!;
        channel.Sources[0].ItemIds = null!;
        var config = WithChannels(channel);
        config.PopularChannel = null!;
        config.StreamDirectory = null!;

        ConfigurationValidator.Validate(config);

        Assert.NotNull(channel.RatingBlocks);
        Assert.NotNull(channel.People);
        Assert.NotNull(channel.Studios);
        Assert.NotNull(channel.Years);
        Assert.Equal(string.Empty, channel.LogoContentType);
        Assert.Equal(string.Empty, channel.AudioLanguage);
        Assert.NotNull(channel.Sources[0].Genres);
        Assert.NotNull(channel.Sources[0].ItemIds);
        Assert.NotNull(config.PopularChannel);
        Assert.Equal(0, config.PopularChannel.Number);
        Assert.Equal(string.Empty, config.StreamDirectory);
        Assert.Empty(channel.EffectiveRatingBlocks());
    }

    [Fact]
    public void NullChannelList_AndNullEntries_AreNormalised()
    {
        var config = new PluginConfiguration { Channels = null! };
        ConfigurationValidator.Validate(config);
        Assert.Empty(config.Channels);

        config = WithChannels(Ch(1));
        config.Channels.Add(null!);
        config.Channels[0].Sources.Add(null!);
        ConfigurationValidator.Validate(config);
        Assert.Single(config.Channels);
        Assert.Single(config.Channels[0].Sources);
    }

    [Fact]
    public void EnabledChannelWithNullSources_ThrowsInsteadOfCrashing()
    {
        var channel = Ch(1);
        channel.Sources = null!;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(WithChannels(channel)));
    }
}
