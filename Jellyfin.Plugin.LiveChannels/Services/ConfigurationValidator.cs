using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Validates incoming plugin configuration before it is persisted. The dashboard enforces these rules in
/// the browser, but configuration can arrive from any API client, so they are enforced server side as well.
/// </summary>
public static class ConfigurationValidator
{
    /// <summary>The largest decoded logo size accepted (headroom over the dashboard's 2 MB cap).</summary>
    public const int MaxLogoBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Validates a configuration, throwing when it must not be persisted.
    /// </summary>
    /// <param name="config">The incoming configuration.</param>
    /// <exception cref="ArgumentException">When an enabled channel is missing a number or sources, a number is duplicated, or a logo is invalid.</exception>
    public static void Validate(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ValidatePlayback(config);

        // The Popular channel takes the same per-channel checks as every configured channel (its number and
        // sources are fixed by the plugin, so only its editable surface is validated).
        if (config.PopularChannel is { } popular)
        {
            ValidateLogo(popular);
            ValidateRatingBlocks(popular);
            ValidateChannelLimits(popular);
        }

        var numbers = new HashSet<int>();
        foreach (var channel in config.Channels)
        {
            ValidateLogo(channel);
            ValidateRatingBlocks(channel);
            ValidateChannelLimits(channel);

            // Serving rules apply only to enabled channels; a disabled channel can be an incomplete draft.
            if (!channel.Enabled)
            {
                continue;
            }

            if (channel.Number <= 0)
            {
                throw new ArgumentException("Enabled channel needs a channel number: " + Describe(channel));
            }

            if (channel.Sources is null || channel.Sources.Count == 0)
            {
                throw new ArgumentException("Enabled channel has no library sources: " + Describe(channel));
            }

            // Two enabled channels with the same number collide in the Live TV guide, so reject duplicates.
            if (!numbers.Add(channel.Number))
            {
                throw new ArgumentException("Duplicate channel number: " + channel.Number);
            }
        }
    }

    // Playback settings that apply to every channel. A value out of range would either be silently clamped or
    // silently ignored at stream time, so it is rejected here instead: a saved setting that does nothing is worse
    // than a save that says why.
    private static void ValidatePlayback(PluginConfiguration config)
    {
        // The output shape. The dashboard only offers sane values, but an API client can send anything, and a
        // zero or negative width reaches the ffmpeg scale filter and fails every subsequent stream at encode
        // time with nothing in the save to explain why.
        if (config.TranscodeWidth is < 320 or > 3840)
        {
            throw new ArgumentException("Resolution width must be between 320 and 3840 pixels.");
        }

        if (config.TranscodeVideoBitrateKbps is < 100 or > 200000)
        {
            throw new ArgumentException("Video bitrate must be between 100 and 200000 kbps.");
        }

        if (config.MaxConcurrentSessions < 0)
        {
            throw new ArgumentException("Maximum concurrent streams cannot be negative. Use 0 for no limit.");
        }

        if (config.SessionTimeoutMinutes < 0)
        {
            throw new ArgumentException("Stream time limit cannot be negative. Use 0 to turn it off.");
        }

        ValidateStreamDirectory(config.StreamDirectory);

        if (config.StartupBufferSeconds != 0
            && config.StartupBufferSeconds is < PluginConfiguration.MinStartupBufferSeconds or > PluginConfiguration.MaxStartupBufferSeconds)
        {
            throw new ArgumentException(
                "Start-up buffer must be between "
                + PluginConfiguration.MinStartupBufferSeconds.ToString(CultureInfo.InvariantCulture)
                + " and "
                + PluginConfiguration.MaxStartupBufferSeconds.ToString(CultureInfo.InvariantCulture)
                + " seconds.");
        }

        // Zero means "never set" (a configuration saved before subtitle styling existed), which renders at the
        // subtitle's own size.
        if (config.SubtitleFontScalePercent != 0
            && config.SubtitleFontScalePercent is < SubtitleStyle.MinScalePercent or > SubtitleStyle.MaxScalePercent)
        {
            throw new ArgumentException(
                "Subtitle size must be between "
                + SubtitleStyle.MinScalePercent.ToString(CultureInfo.InvariantCulture)
                + "% and "
                + SubtitleStyle.MaxScalePercent.ToString(CultureInfo.InvariantCulture)
                + "%.");
        }

        ValidateColor(config.SubtitleTextColor, "Subtitle text colour");
        ValidateColor(config.SubtitleOutlineColor, "Subtitle outline colour");
    }

    private static void ValidateColor(string? value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value) && !SubtitleStyle.TryConvertColor(value, out _))
        {
            throw new ArgumentException(label + " must be a hex colour like #FFFFFF.");
        }
    }

    // The stream directory is destructively managed: the plugin marks it as its own and sweeps its session
    // directories out of it. A configured path must therefore never point into existing data, so only a new,
    // empty, or already-plugin-owned directory is accepted, and the refusal happens here at save time, where
    // the admin can still pick a different folder.
    private static void ValidateStreamDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var path = configured.Trim();
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException("Stream file location must be an absolute path.");
        }

        bool foreign;
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full) || File.Exists(Path.Combine(full, ChannelService.StreamRootMarkerName)))
            {
                // A missing directory is created (and marked) on first use; a marked one is already ours.
                return;
            }

            foreign = Directory.EnumerateFileSystemEntries(full).Any(entry => !IsPluginStreamEntry(entry));
        }
        catch (Exception)
        {
            // Unreadable or malformed paths are left to stream time, which reports its own clear failure.
            return;
        }

        if (foreign)
        {
            throw new ArgumentException(
                "Stream file location must be a new or empty directory. The plugin cleans up old stream files inside its folder, so it refuses a directory that already contains other content: " + path);
        }
    }

    // Whether an entry inside a candidate stream root is plugin-shaped, which identifies a stream root written
    // by a version before the marker file existed: session directories, the schedule cache, or the legacy
    // single-file schedule.
    private static bool IsPluginStreamEntry(string entry)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
        if (Directory.Exists(entry))
        {
            return string.Equals(name, ChannelService.ScheduleDirName, StringComparison.Ordinal)
                || LiveChannelsTvService.IsSessionDir(entry);
        }

        return string.Equals(name, "schedule.json", StringComparison.Ordinal);
    }

    // Per-channel numeric limits the dashboard clamps in the browser; enforced here for every other client so
    // an out-of-range value is rejected with a reason instead of silently misbehaving at schedule time.
    private static void ValidateChannelLimits(Channel channel)
    {
        if (channel.TransitionWindowMinutes < 0)
        {
            throw new ArgumentException("Transition window cannot be negative: " + Describe(channel));
        }

        if (channel.EpisodesPerBlock < 1)
        {
            throw new ArgumentException("Episodes per block must be at least 1: " + Describe(channel));
        }

        if (channel.MinCommunityRating is < 0 or > 10)
        {
            throw new ArgumentException("Minimum community rating must be between 0 and 10: " + Describe(channel));
        }

        if (channel.MinCriticRating is < 0 or > 100)
        {
            throw new ArgumentException("Minimum critic rating must be between 0 and 100: " + Describe(channel));
        }
    }

    // A custom rating-block window must sit within the day and cover a real span; an all-day block ignores its
    // times. A zero-length custom window would silently never apply, so it is rejected as a mistake.
    private static void ValidateRatingBlocks(Channel channel)
    {
        if (channel.RatingBlocks is null)
        {
            return;
        }

        foreach (var block in channel.RatingBlocks)
        {
            if (block.Period != RatingBlockPeriod.Custom)
            {
                continue;
            }

            if (block.StartMinutes is < 0 or > 1439 || block.EndMinutes is < 0 or > 1439)
            {
                throw new ArgumentException("Rating block time must be between 00:00 and 23:59: " + Describe(channel));
            }

            if (block.StartMinutes == block.EndMinutes)
            {
                throw new ArgumentException("Custom rating block needs different start and end times: " + Describe(channel));
            }
        }
    }

    private static string Describe(Channel channel)
        => string.IsNullOrWhiteSpace(channel.Name) ? "channel " + channel.Number : channel.Name;

    private static void ValidateLogo(Channel channel)
    {
        if (string.IsNullOrEmpty(channel.LogoData))
        {
            return;
        }

        // Reject oversized input before decoding so a huge string can't force a large transient allocation.
        // Base64 expands by ~4/3, so the encoded form of the limit is that many characters.
        if (channel.LogoData.Length > (((MaxLogoBytes / 3) + 1) * 4))
        {
            throw new ArgumentException("Channel logo exceeds the 4 MB limit: " + channel.Name);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(channel.LogoData);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Channel logo is not valid Base64: " + channel.Name);
        }

        if (bytes.Length > MaxLogoBytes)
        {
            throw new ArgumentException("Channel logo exceeds the 4 MB limit: " + channel.Name);
        }
    }
}
