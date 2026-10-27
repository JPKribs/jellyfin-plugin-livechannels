using System.IO;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Where the plugin keeps its files. Two kinds, kept apart so clearing Jellyfin's cache never changes what a
/// channel airs: what must survive a restart or a cache clear (the schedules, whose splice anchors keep the
/// program on air in place, and the item lengths learned by playing them) lives in Jellyfin's data folder; what
/// can always be rebuilt (stream segments, tune-in cards, Up Next bumpers, generated logos, extracted fonts and
/// ffmpeg's working files) lives in the stream root, which is the configured stream directory or a folder in
/// Jellyfin's cache.
/// </summary>
internal static class PluginPaths
{
    /// <summary>The name of the stream-root folder holding rebuildable assets (cards, bumpers, logos, fonts).</summary>
    public const string AssetsDirName = "assets";

    /// <summary>The name of the stream-root folder holding ffmpeg's short-lived working files.</summary>
    public const string WorkDirName = "work";

    /// <summary>The plugin's folder in Jellyfin's data folder, for files that must survive a restart or a cache clear.</summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The data folder.</returns>
    public static string DataDir(IApplicationPaths paths) => Path.Combine(paths.DataPath, "livechannels");

    /// <summary>The folder for rebuildable assets, inside the stream root.</summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The assets folder.</returns>
    public static string AssetsDir(IApplicationPaths paths) => Path.Combine(ChannelService.ResolveStreamRoot(paths), AssetsDirName);

    /// <summary>The folder for ffmpeg's short-lived working files (concat playlists), inside the stream root.</summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The working folder.</returns>
    public static string WorkDir(IApplicationPaths paths) => Path.Combine(ChannelService.ResolveStreamRoot(paths), WorkDirName);

    /// <summary>Where versions before 12.4.1 kept cards, logos and the icon font, cleared out once at start-up.</summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The legacy assets folder.</returns>
    public static string LegacyAssetsDir(IApplicationPaths paths) => Path.Combine(paths.CachePath, "livechannels-assets");
}
