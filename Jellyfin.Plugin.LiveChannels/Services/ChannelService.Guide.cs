using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: keeping Jellyfin's stored guide in step with the schedule this plugin publishes.
public partial class ChannelService
{
    /// <summary>
    /// Deletes the guide programs Jellyfin still holds for one of this plugin's channels that the refresh now in
    /// progress no longer publishes. Jellyfin only prunes stale programs when every channel of every tuner
    /// refreshes without error, so a single failing channel anywhere leaves the previous refresh's programs in
    /// place beside the new ones, and the guide shows two schedules overlapping. Pruning per channel, as each
    /// channel is refreshed, keeps this plugin's guide exact regardless of what else failed.
    /// </summary>
    /// <param name="serviceName">The live-TV service name the channel belongs to.</param>
    /// <param name="channelId">The channel's external id.</param>
    /// <param name="keep">The external program ids this refresh publishes for the channel.</param>
    internal void PurgeStaleGuidePrograms(string serviceName, string channelId, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        try
        {
            var channel = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel],
                DtoOptions = new DtoOptions(false)
            })
                .OfType<LiveTvChannel>()
                .FirstOrDefault(c => string.Equals(c.ExternalId, channelId, StringComparison.Ordinal)
                    && string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
            {
                return; // First refresh: Jellyfin has not stored the channel (or any of its programs) yet.
            }

            var stale = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                ChannelIds = [channel.Id],
                DtoOptions = new DtoOptions(false)
            })
                .OfType<LiveTvProgram>()
                .Where(p => p.ExternalId is null || !keep.Contains(p.ExternalId))
                .ToList();

            // One batch, not a write per programme: every rebuild moves the slots after the item airing, so most of
            // a channel's stored guide goes stale each day, and tens of thousands of single-row deletes interleaved
            // with Jellyfin's own guide writes held the database lock for minutes. Guide programmes have no
            // children and no files, which is exactly what the fast batch delete is for.
            if (stale.Count > 0)
            {
                _libraryManager.DeleteItemsUnsafeFast(stale, false);
                _logger.LogInformation("Live Channels: removed {Count} stale guide program(s) from channel {Name}", stale.Count, channel.Name);
            }
        }
        catch (Exception ex)
        {
            // Best effort: Jellyfin's own clean-up still prunes them on a refresh where nothing failed.
            _logger.LogWarning(ex, "Live Channels: could not prune stale guide programs for channel {Id}", channelId);
        }
    }
}
