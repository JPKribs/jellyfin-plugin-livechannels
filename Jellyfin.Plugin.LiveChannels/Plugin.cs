using System;
using System.Collections.Generic;
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
        logger.LogInformation("Live Channels plugin initialized");
    }

    /// <inheritdoc />
    public override string Name => "Live Channels";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ac6940fb-aac6-4de8-b622-55a662e23658");

    /// <inheritdoc />
    public override string Description =>
        "Build looping virtual TV channels from genres and ratings or hand-picked items, presented natively in Jellyfin's Live TV with no exposed endpoints.";

    /// <summary>
    /// Validates incoming configuration before persisting it. The dashboard enforces the same rules in the
    /// browser, but configuration can arrive from any API client.
    /// </summary>
    /// <param name="configuration">The incoming configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            ConfigurationValidator.Validate(config);
        }

        base.UpdateConfiguration(configuration);
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
            EnableInMainMenu = false
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
            Name = "livechannels_symbols.ttf",
            EmbeddedResourcePath = $"{ns}.Assets.MaterialSymbolsOutlined.ttf"
        };

        // Shared base CSS and JS compiled in from the JPKribs.Jellyfin.Base package.
        foreach (var page in GetSharedPages("livechannels"))
        {
            yield return page;
        }
    }
}
