using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: the built-in "Popular" channel -- its synthetic Channel and the watch/recency/rating buckets.
public partial class ChannelService
{
    // ---- Built-in "Popular" channel (channel 0) ----

    /// <summary>The stable id of the built-in Popular channel.</summary>
    public const string PopularChannelId = "livechannels-popular";

    // Per-bucket quotas for the Popular channel population (35 titles). Movies (25): most recently played (by play
    // date, across every user), recently added, and a random handful from the highest community-rated. Shows (10):
    // most recently played series, series whose episodes were just added, and a random handful from the highest
    // community-rated. A short or empty source just yields fewer items -- the buckets do not backfill each other, so
    // the channel is simply smaller on a sparse server. Each selected series contributes its whole catalogue; the
    // loop builder then caps every series to one block per loop, so all ten shows appear and none can dominate.
    private const int MovieWatched = 15;
    private const int MovieRecent = 5;
    private const int MovieRandomRated = 5;
    private const int SeriesWatched = 6;
    private const int SeriesRecentEpisode = 2;
    private const int SeriesRandomRated = 2;

    // The community-rated pools the random "rated" picks are drawn from (the top N by rating, then a seeded
    // random subset), so the channel surfaces different highly-rated titles over time rather than the same few.
    private const int MovieRatedPool = 50;
    private const int SeriesRatedPool = 25;

    // How many of each user's most-recently-played episodes to scan when finding the recently-played series.
    private const int WatchedEpisodeScan = 200;

    private static bool PopularChannelEnabled()
        => Plugin.Instance?.ReadConfiguration(c => c.PopularChannel.Enabled) ?? false;

    // The Popular channel as Jellyfin sees it: the user's saved settings (name, icon, rating band, subtitle rule,
    // loop behaviour) with the fixed bits forced. It always lives at channel 0 under the reserved id, and its
    // content comes from the popular buckets rather than configured sources. Returns a copy so the stored
    // configuration object is never mutated.
    private static Channel BuildPopularChannel()
    {
        var src = Plugin.Instance?.ReadConfiguration(c => c.PopularChannel) ?? new Channel();
        return new Channel
        {
            Id = PopularChannelId,
            Number = 0,
            Name = string.IsNullOrWhiteSpace(src.Name) ? "Popular" : src.Name,
            LogoStyle = src.LogoStyle,
            LogoSymbol = src.LogoSymbol,
            LogoData = src.LogoData,
            LogoContentType = src.LogoContentType,
            LogoShowName = src.LogoShowName,
            Enabled = src.Enabled,
            AudioLanguage = src.AudioLanguage,
            MinOfficialRating = src.MinOfficialRating,
            MaxOfficialRating = src.MaxOfficialRating,
            IncludeUnrated = src.IncludeUnrated,
            KidsRatingThreshold = src.KidsRatingThreshold,
            Years = src.Years,
            MinCommunityRating = src.MinCommunityRating,
            MinCriticRating = src.MinCriticRating,
            Studios = src.Studios,
            People = src.People,
            EpisodesPerBlock = src.EpisodesPerBlock,
            KeepMultiPartTogether = src.KeepMultiPartTogether,
            IncludeSpecials = src.IncludeSpecials,
            Shuffle = src.Shuffle,
            LoopMode = src.LoopMode,
            ShuffleEpisodes = src.ShuffleEpisodes,
            FavorKind = src.FavorKind,
            FavorStrength = src.FavorStrength,
            SubtitleBurnIn = src.SubtitleBurnIn
        };
    }

    // Resolves the Popular channel's loop. Movies and shows are each drawn from three buckets (most recently played
    // across all users, recent additions, top community rating) up to their per-bucket caps, de-duplicated so a
    // title that qualifies in two buckets is counted once and the next candidate backfills it. Series then expand
    // to their episodes, and the loop builder caps each series to one block per loop.
    private IReadOnlyList<ProgramEntry> ResolvePopularPrograms(Channel channel)
    {
        var ratings = new RatingFilter(
            ResolveRatingScore(channel.MinOfficialRating),
            ResolveRatingScore(channel.MaxOfficialRating),
            channel.IncludeUnrated);
        var kidsScore = ResolveRatingScore(channel.KidsRatingThreshold);
        var years = new YearFilter(channel.Years);
        var kinds = PlayableKinds;
        // The Popular channel has no library sources, so studios and people are resolved across every library.
        var studios = BuildStudioSet(channel.Studios);
        var seriesStudios = studios.Count > 0 ? BuildSeriesStudioMap(Array.Empty<Guid>()) : new Dictionary<Guid, HashSet<string>>();
        var peopleAllowed = channel.People.Any(p => p.Id != Guid.Empty)
            ? ResolvePeopleAllowed(channel.People, Array.Empty<Guid>(), kinds)
            : null;
        var users = _userManager.GetUsers().ToList();
        var lookup = new Dictionary<Guid, BaseItem>();

        // A per-resolve seed for the random "rated" picks. It is captured once and baked into the cached schedule
        // (which both the guide and the live stream read), so they always agree; it changes each guide refresh, so
        // the random picks rotate over time. Includes the day so the rotation is stable within a refresh cycle.
        var seed = PopularChannelId + ":" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // Keep only the candidates the channel's rating band allows, so the rating cap applies to the bucket
        // selection itself. The queries over-fetch so a tight cap still leaves enough to fill the quotas.
        List<Guid> Ids(IReadOnlyList<BaseItem> found)
        {
            var ids = new List<Guid>();
            foreach (var item in found)
            {
                if (!ratings.Allows(item))
                {
                    continue;
                }

                lookup[item.Id] = item;
                ids.Add(item.Id);
            }

            return ids;
        }

        // Movies (25): 15 most-recently-played, 5 most-recently-added, 5 random from the top community-rated.
        // SelectBuckets de-duplicates across buckets and fills each in order; buckets do not backfill each other.
        var movieIds = SelectBuckets(
            (Ids(RecentlyPlayedMovies(users, MovieWatched * 4)), MovieWatched),
            (Ids(Recent(BaseItemKind.Movie, MovieRecent * 4)), MovieRecent),
            (SeededShuffle(Ids(TopRated(BaseItemKind.Movie, MovieRatedPool)), seed + ":mvr"), MovieRandomRated));

        // Shows (10): 6 most-recently-played series, 2 series whose episodes were just added, 2 random from the top
        // rated. The recently-played series are found by walking the newest-played episodes until 6 distinct series.
        var seriesIds = SelectBuckets(
            (Ids(RecentlyPlayedSeries(users, SeriesWatched * 4)), SeriesWatched),
            (Ids(RecentEpisodeSeries(SeriesRecentEpisode * 4)), SeriesRecentEpisode),
            (SeededShuffle(Ids(TopRated(BaseItemKind.Series, SeriesRatedPool)), seed + ":svr"), SeriesRandomRated));

        var items = new Dictionary<Guid, BaseItem>();
        foreach (var id in movieIds)
        {
            if (lookup.TryGetValue(id, out var movie))
            {
                items[movie.Id] = movie;
            }
        }

        foreach (var id in seriesIds)
        {
            foreach (var episode in EpisodesOf(id))
            {
                items[episode.Id] = episode;
            }
        }

        var entries = new List<ProgramEntry>();
        foreach (var item in items.Values)
        {
            if (!channel.IncludeSpecials && item is Episode ep && ep.ParentIndexNumber == 0)
            {
                continue;
            }

            // Episodes inherit their series' rating, but re-check so the cap holds for any odd outlier.
            if (!ratings.Allows(item))
            {
                continue;
            }

            // Apply the same channel-wide filters as a normal channel to each playable item (the episode's own year,
            // not the series' start year), so a filtered Popular channel keeps only the matching movies and episodes.
            if (!years.Allows(item.ProductionYear)
                || !PassesMinRating(item.CommunityRating, channel.MinCommunityRating)
                || !PassesMinRating(item.CriticRating, channel.MinCriticRating)
                || !PassesStudios(studios, EffectiveStudios(item, seriesStudios))
                || (peopleAllowed is not null && !peopleAllowed.Contains(item.Id)))
            {
                continue;
            }

            var entry = ToEntry(item, kidsScore, SafeGetMediaStreams(item.Id));
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var options = new ChannelLoopOptions(
            channel.EpisodesPerBlock,
            channel.KeepMultiPartTogether,
            channel.EffectiveLoopMode(),
            channel.ShuffleEpisodes,
            PopularChannelId,
            channel.FavorKind,
            channel.FavorStrength,
            LoopRotation());

        return ProgramLoopBuilder.Build(entries, options);
    }

    /// <summary>
    /// Fills ordered buckets up to their quotas from candidate id lists, de-duplicating across buckets: an id
    /// already taken by an earlier bucket is skipped and the next candidate backfills it. A bucket whose
    /// candidates run out simply contributes fewer ids.
    /// </summary>
    /// <param name="buckets">The candidate id lists paired with their quotas, in priority order.</param>
    /// <returns>The selected ids, each appearing once, in bucket order.</returns>
    public static List<Guid> SelectBuckets(params (IReadOnlyList<Guid> Candidates, int Quota)[] buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var selected = new HashSet<Guid>();
        var result = new List<Guid>();
        foreach (var (candidates, quota) in buckets)
        {
            var taken = 0;
            foreach (var id in candidates)
            {
                if (taken >= quota)
                {
                    break;
                }

                if (selected.Add(id))
                {
                    result.Add(id);
                    taken++;
                }
            }
        }

        return result;
    }

    // Most recently added items of a kind, across every library.
    private IReadOnlyList<BaseItem> Recent(BaseItemKind kind, int count)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            Limit = count
        });

    // Highest community-rated items of a kind, across every library.
    private IReadOnlyList<BaseItem> TopRated(BaseItemKind kind, int count)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            Limit = count
        });

    // Movies watched most across the whole server: each user's most-played movies are pooled, then ranked by
    // their play count summed over every user.
    // Movies played most recently across the whole server. Each movie's play time is the most recent across every
    // user, and the newest-played `count` are returned. As anyone watches a movie it rises to the top, so the
    // channel reflects what the server has been watching lately rather than all-time totals.
    private List<BaseItem> RecentlyPlayedMovies(List<User> users, int count)
    {
        if (users.Count == 0)
        {
            return new List<BaseItem>();
        }

        // Movie id -> (most recent play across users, the movie).
        var played = new Dictionary<Guid, (DateTime When, BaseItem Item)>();
        foreach (var user in users)
        {
            foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = count * 5
            }))
            {
                var when = _userDataManager.GetUserData(user, movie)?.LastPlayedDate;
                if (when is { } w && (!played.TryGetValue(movie.Id, out var current) || w > current.When))
                {
                    played[movie.Id] = (w, movie);
                }
            }
        }

        return played.Values
            .OrderByDescending(p => p.When)
            .Take(count)
            .Select(p => p.Item)
            .ToList();
    }

    // Series played most recently across the whole server. Every user's recently-played episodes are scanned, each
    // episode's play time is taken as the most recent across users, and we walk down that newest-played-first list
    // taking each new series until we have `count` distinct ones -- so the popular shows are whatever was watched
    // most recently. The whole series is used downstream, not just those episodes, and as people keep watching the
    // schedule refreshes onto the latest shows.
    private List<BaseItem> RecentlyPlayedSeries(List<User> users, int count)
    {
        if (users.Count == 0)
        {
            return new List<BaseItem>();
        }

        // Episode id -> (most recent play across users, owning series).
        var episodePlayed = new Dictionary<Guid, (DateTime When, Guid SeriesId)>();
        foreach (var user in users)
        {
            foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = WatchedEpisodeScan
            }))
            {
                if (item is Episode ep && ep.SeriesId != Guid.Empty)
                {
                    var when = _userDataManager.GetUserData(user, item)?.LastPlayedDate;
                    if (when is { } w && (!episodePlayed.TryGetValue(item.Id, out var current) || w > current.When))
                    {
                        episodePlayed[item.Id] = (w, ep.SeriesId);
                    }
                }
            }
        }

        var series = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        foreach (var entry in episodePlayed.Values.OrderByDescending(e => e.When))
        {
            if (!seen.Add(entry.SeriesId))
            {
                continue;
            }

            if (_libraryManager.GetItemById(entry.SeriesId) is { } found)
            {
                series.Add(found);
                if (series.Count >= count)
                {
                    break;
                }
            }
        }

        return series;
    }

    // Series whose episodes were added to the library most recently: the newest episodes are scanned and their
    // distinct owning series returned, newest first, until `count` are found. Surfaces "what just landed" as shows.
    private List<BaseItem> RecentEpisodeSeries(int count)
    {
        var series = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            Limit = Math.Max(count, 1) * 50
        }))
        {
            if (item is Episode ep && ep.SeriesId != Guid.Empty && seen.Add(ep.SeriesId)
                && _libraryManager.GetItemById(ep.SeriesId) is { } found)
            {
                series.Add(found);
                if (series.Count >= count)
                {
                    break;
                }
            }
        }

        return series;
    }

    // A deterministic, seeded shuffle of ids: stable for a given seed (so the guide and stream agree and it only
    // changes when the seed does), used to pick a random subset from a "top rated" pool.
    private static List<Guid> SeededShuffle(IReadOnlyList<Guid> ids, string seed)
        => ids.OrderBy(id => SeededOrder(seed, id)).ToList();

    // FNV-1a hash of the seed and an id, giving a stable pseudo-random ordering key.
    private static uint SeededOrder(string seed, Guid id)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in seed)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            foreach (var b in id.ToByteArray())
            {
                hash = (hash ^ b) * 16777619u;
            }

            return hash;
        }
    }
}
