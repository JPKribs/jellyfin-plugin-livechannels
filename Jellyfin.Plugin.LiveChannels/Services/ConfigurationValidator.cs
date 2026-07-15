using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;

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
