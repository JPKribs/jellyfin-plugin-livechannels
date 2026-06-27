using System;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="ConfigurationValidator"/> — the server-side guard against bad config from any API
/// client (duplicate channel numbers, malformed or oversized logos).
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
}
