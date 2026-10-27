using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels;

/// <summary>
/// Main plugin entry point for Live Channels.
/// </summary>
public class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _applicationPaths = applicationPaths;

        // Time-of-day schedules chain from the last configuration save. An install upgrading past that feature
        // has no recorded save yet, so stamp one now; without it every build would have to invent an anchor.
        MutateConfiguration(config =>
        {
            if (config.ScheduleAnchorUtc != default)
            {
                return false;
            }

            config.ScheduleAnchorUtc = DateTime.UtcNow;
            return true;
        });

        logger.LogInformation("Live Channels plugin initialized");
    }

    private readonly IApplicationPaths _applicationPaths;

    /// <inheritdoc />
    public override string Name => "Live Channels";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ac6940fb-aac6-4de8-b622-55a662e23658");

    /// <inheritdoc />
    public override string Description =>
        "Build looping virtual TV channels from genres and ratings or hand-picked items, presented natively in Jellyfin's Live TV with no exposed endpoints.";

    /// <summary>
    /// Validates incoming configuration before persisting it. The dashboard enforces the same rules in the
    /// browser, but configuration can come from any API client. Runs under the configuration lock.
    /// </summary>
    /// <param name="incoming">The incoming configuration.</param>
    /// <param name="stored">The configuration currently in effect.</param>
    protected override void OnConfigurationSaving(PluginConfiguration incoming, PluginConfiguration stored)
    {
        ConfigurationValidator.Validate(incoming, stored);

        // A save that changes the channels re-anchors the time-of-day schedule chain (the cache below is dropped
        // anyway, so the reshuffle this causes coincides with the rebuild the user already expects from saving).
        // A save that only touches plugin-wide settings (bitrate, codec, subtitle look) keeps the anchor, so the
        // guide does not reshuffle every time-of-day channel for a change that has nothing to do with them.
        incoming.ScheduleAnchorUtc = AffectsSchedule(incoming, stored) || stored.ScheduleAnchorUtc == default
            ? DateTime.UtcNow
            : stored.ScheduleAnchorUtc;

        // A save that changes what the schedules hold must not be served from them, so take every cached schedule
        // out of service; the next guide refresh or tune-in rebuilds it from the new configuration, spliced onto
        // the old one so what is airing keeps its slot. A save that changes nothing they hold (bitrate, codec,
        // subtitle look) leaves them alone. This runs before the save on purpose: the cache lives under the stream
        // directory, so the one cleared is the directory the old schedules were written to.
        if (InvalidatesSchedules(incoming, stored))
        {
            ChannelService.ClearScheduleCache(_applicationPaths);
        }
    }

    /// <summary>
    /// Whether a save changes anything a cached schedule holds: the channels themselves (see
    /// <see cref="AffectsSchedule"/>), the default language (it picks each item's audio track for channels without
    /// their own), or the stream directory the cache lives under.
    /// </summary>
    /// <param name="incoming">The configuration about to be saved.</param>
    /// <param name="stored">The configuration currently in effect.</param>
    /// <returns>Whether the cached schedules must be rebuilt.</returns>
    internal static bool InvalidatesSchedules(PluginConfiguration incoming, PluginConfiguration stored)
        => AffectsSchedule(incoming, stored)
            || !string.Equals(incoming.DefaultSubtitleLanguage?.Trim(), stored.DefaultSubtitleLanguage?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incoming.StreamDirectory?.Trim(), stored.StreamDirectory?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Whether a save changes what the channels schedule: any channel's definition (its sources, filters, order,
    /// rating blocks, enabled state) or the Popular channel's. Compared by value, so a save that sends the same
    /// channels back unchanged does not count.
    /// </summary>
    /// <param name="incoming">The configuration about to be saved.</param>
    /// <param name="stored">The configuration currently in effect.</param>
    /// <returns>Whether the schedule-defining settings differ.</returns>
    internal static bool AffectsSchedule(PluginConfiguration incoming, PluginConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);
        return !string.Equals(JsonSerializer.Serialize(incoming.Channels), JsonSerializer.Serialize(stored.Channels), StringComparison.Ordinal)
            || !string.Equals(JsonSerializer.Serialize(incoming.PopularChannel), JsonSerializer.Serialize(stored.PopularChannel), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        yield return new PluginPageInfo
        {
            Name = "livechannels_channels",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_channels.html",
            MenuSection = "server",
            DisplayName = "Live Channels",
            EnableInMainMenu = false,
            MenuIcon = "live_tv"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_channels.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_channels.js"
        };

        // Tab 2: Popular channel (the built-in channel 0's own settings).
        yield return new PluginPageInfo
        {
            Name = "livechannels_popular",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_popular.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_popular.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_popular.js"
        };

        // Tab 3: Settings (plugin-wide configuration).
        yield return new PluginPageInfo
        {
            Name = "livechannels_settings",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_settings.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_settings.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_settings.js"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_sessions",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_sessions.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_sessions.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_sessions.js"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_symbols.ttf",
            EmbeddedResourcePath = $"{ns}.Assets.MaterialSymbolsOutlined.ttf"
        };

        foreach (var page in GetSharedPages("livechannels"))
        {
            yield return page;
        }
    }
}
