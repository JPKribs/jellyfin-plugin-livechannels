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
    public void ValidBase64Logo_DoesNotThrow()
    {
        var channel = Ch(1);
        channel.LogoData = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        ConfigurationValidator.Validate(WithChannels(channel));
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
    public void StartupBuffer_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { StartupBufferSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(new PluginConfiguration { StartupBufferSeconds = 600 }));
    }

    [Fact]
    public void StartupBuffer_UnsetOrInRange_IsAccepted()
    {
        ConfigurationValidator.Validate(new PluginConfiguration { StartupBufferSeconds = 0 });
        ConfigurationValidator.Validate(new PluginConfiguration { StartupBufferSeconds = 30 });
    }

    [Fact]
    public void EffectiveStartupBuffer_ClampsWhatWasStored()
    {
        Assert.Equal(PluginConfiguration.DefaultStartupBufferSeconds, new PluginConfiguration().EffectiveStartupBufferSeconds());
        Assert.Equal(PluginConfiguration.DefaultStartupBufferSeconds, new PluginConfiguration { StartupBufferSeconds = 0 }.EffectiveStartupBufferSeconds());
        Assert.Equal(PluginConfiguration.DefaultStartupBufferSeconds, new PluginConfiguration { StartupBufferSeconds = -5 }.EffectiveStartupBufferSeconds());
        Assert.Equal(PluginConfiguration.MinStartupBufferSeconds, new PluginConfiguration { StartupBufferSeconds = 1 }.EffectiveStartupBufferSeconds());
        Assert.Equal(PluginConfiguration.MaxStartupBufferSeconds, new PluginConfiguration { StartupBufferSeconds = 5000 }.EffectiveStartupBufferSeconds());
        Assert.Equal(20, new PluginConfiguration { StartupBufferSeconds = 20 }.EffectiveStartupBufferSeconds());
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
}
