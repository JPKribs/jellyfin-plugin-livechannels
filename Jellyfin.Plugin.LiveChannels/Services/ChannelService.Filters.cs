using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: the per-item content filters (HDR/DV guards, rating, year, studio) applied while resolving.
public partial class ChannelService
{
    // Whether the video is HDR (PQ or HLG), keyed off the colour transfer: smpte2084 = HDR10/PQ,
    // arib-std-b67 = HLG.
    internal static bool ComputeIsHdr(MediaStream? video)
    {
        var transfer = video?.ColorTransfer;
        return string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }

    // Whether the video is Dolby Vision Profile 5, the one DV flavour with no HDR10-compatible base layer:
    // its IPT-colour frames render green/purple through every tone mapper (VPP and software alike), so such
    // items are excluded from schedules with a loud log instead of airing broken.
    internal static bool IsDolbyVisionProfile5(MediaStream? video)
        => video?.DvProfile == 5;

    // A rating name as a numeric score, or null when the name is empty or unknown.
    private int? ResolveRatingScore(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return _localization.GetRatingScore(name)?.Score;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not rank rating {Rating}", name);
            return null;
        }
    }

    // The channel's rating bounds. An item passes when it is rated within [Min, Max], or when it is unrated and
    // unrated content is included. An absent bound is open on that side.
    private readonly record struct RatingFilter(int? Min, int? Max, bool IncludeUnrated)
    {
        public bool Allows(BaseItem item)
        {
            var value = item.InheritedParentalRatingValue;
            if (value is null)
            {
                return IncludeUnrated;
            }

            if (Min is not null && value.Value < Min.Value)
            {
                return false;
            }

            return Max is null || value.Value <= Max.Value;
        }
    }

    // The channel's allowed production years. An empty set allows every year; otherwise an item passes only when
    // its production year is one of these, so an item with no year is dropped while a year filter is active.
    // Internal so the year matching can be unit tested without a live library. Takes the item's production year
    // directly (rather than a BaseItem) for the same reason.
    internal readonly struct YearFilter
    {
        private readonly HashSet<int>? _years;

        public YearFilter(IEnumerable<int>? years)
        {
            var set = years is null ? null : new HashSet<int>(years);
            _years = set is { Count: > 0 } ? set : null;
        }

        // Whether any year is actually restricted (a non-empty set was supplied).
        public bool IsActive => _years is not null;

        public bool Allows(int? productionYear)
            => _years is null || (productionYear is int year && _years.Contains(year));
    }

    // Whether a 0-N rating passes a minimum floor. A floor of zero (or less) is off and admits everything;
    // otherwise the item must carry a rating at or above the floor, so a missing rating is dropped. Shared by the
    // community (0-10) and critic (0-100) floors. Internal so the threshold logic can be unit tested.
    internal static bool PassesMinRating(float? value, double minimum)
        => minimum <= 0 || (value is float rating && rating >= minimum);

    // Whether an item's studios satisfy the channel's studio set. An empty channel set admits everything; otherwise
    // the item passes when any of its (effective) studios is in the set. Internal so the matching can be unit tested.
    internal static bool PassesStudios(IReadOnlySet<string> channelStudios, IEnumerable<string> effectiveStudios)
    {
        if (channelStudios.Count == 0)
        {
            return true;
        }

        foreach (var studio in effectiveStudios)
        {
            if (!string.IsNullOrEmpty(studio) && channelStudios.Contains(studio))
            {
                return true;
            }
        }

        return false;
    }

    // The channel's studio names as a case-insensitive set, blanks removed. Empty when no studio filter is set.
    private static HashSet<string> BuildStudioSet(IEnumerable<string>? studios)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (studios is not null)
        {
            foreach (var studio in studios)
            {
                if (!string.IsNullOrWhiteSpace(studio))
                {
                    set.Add(studio.Trim());
                }
            }
        }

        return set;
    }

    // A seriesId -> studios lookup for every series in the given libraries (all libraries when none are listed), so
    // an episode's effective studios can include its parent series' network the way the genre filter does. Built
    // only when a studio filter is active.
    private Dictionary<Guid, HashSet<string>> BuildSeriesStudioMap(IReadOnlyCollection<Guid> libraryIds)
    {
        var map = new Dictionary<Guid, HashSet<string>>();

        void Add(InternalItemsQuery query)
        {
            foreach (var series in _libraryManager.GetItemList(query))
            {
                map[series.Id] = new HashSet<string>(series.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            }
        }

        if (libraryIds.Count == 0)
        {
            Add(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true, IsVirtualItem = false });
        }
        else
        {
            foreach (var libraryId in libraryIds)
            {
                Add(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, AncestorIds = new[] { libraryId }, Recursive = true, IsVirtualItem = false });
            }
        }

        return map;
    }

    // An item's effective studios for matching: its own, plus its series' when it is an episode.
    private static IEnumerable<string> EffectiveStudios(BaseItem item, Dictionary<Guid, HashSet<string>> seriesStudios)
    {
        var own = item.Studios ?? Array.Empty<string>();
        if (item is Episode ep && ep.SeriesId != Guid.Empty && seriesStudios.TryGetValue(ep.SeriesId, out var ss))
        {
            return own.Concat(ss);
        }

        return own;
    }
}
