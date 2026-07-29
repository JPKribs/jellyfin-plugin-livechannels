using System;
using System.Collections.Generic;
using System.Globalization;
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

        var numbers = new HashSet<int>();
        foreach (var channel in config.Channels)
        {
            ValidateLogo(channel);
            ValidateRatingBlocks(channel);

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
