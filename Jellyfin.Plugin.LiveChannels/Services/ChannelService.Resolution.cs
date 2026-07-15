using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: resolving a channel's configured sources into the ordered, probed program loop.
public partial class ChannelService
{
    // Reads an item's media streams once, returning an empty list (never throwing) when they cannot be read, so
    // the guide-refresh build can probe every item's metadata with a single query per item and tolerate gaps.
    private IReadOnlyList<MediaStream> SafeGetMediaStreams(Guid itemId)
    {
        try
        {
            return _mediaSourceManager.GetMediaStreams(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for {ItemId}", itemId);
            return Array.Empty<MediaStream>();
        }
    }

    // The default audio track (the one Jellyfin would play) and its ordinal among the item's audio streams, or
    // null when the item has no audio.
    private static (int Ordinal, MediaStream Stream)? DefaultAudio(IReadOnlyList<MediaStream> streams)
    {
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        var defaultIndex = audio.FindIndex(s => s.IsDefault);
        var ordinal = defaultIndex >= 0 ? defaultIndex : 0;
        return (ordinal, audio[ordinal]);
    }

    // The item's subtitle streams (ordered by absolute index) as the minimal burn-in descriptors cached on the
    // schedule, so the live stream picks a burn-in track without re-reading the media streams.
    private static SubtitleStreamInfo[] BuildSubtitleInfos(IReadOnlyList<MediaStream> streams)
    {
        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
        if (subtitles.Count == 0)
        {
            return Array.Empty<SubtitleStreamInfo>();
        }

        var infos = new SubtitleStreamInfo[subtitles.Count];
        for (var i = 0; i < subtitles.Count; i++)
        {
            var s = subtitles[i];
            infos[i] = new SubtitleStreamInfo
            {
                RelativeIndex = i,
                AbsoluteIndex = s.Index,
                IsForced = s.IsForced,
                IsDefault = s.IsDefault,
                IsText = s.IsTextSubtitleStream
            };
        }

        return infos;
    }

    // Whether the default audio track is in the channel's required language (a three-letter ISO code). An empty
    // language allows everything. Strict by design ("MUST be this language"): an item whose default track is
    // another language, is untagged, or cannot be read is excluded. Operates on the already-read streams.
    private static bool AudioLanguageAllows(string language, IReadOnlyList<MediaStream> streams)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var def = DefaultAudio(streams);
        return def is not null && string.Equals(def.Value.Stream.Language, language, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a channel's items into the ordered, schedulable loop it cycles through. Content is the union
    /// of every library source; items without a playable file or a positive runtime are dropped because they
    /// cannot be placed on the timeline.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The ordered program loop.</returns>
    private IReadOnlyList<ProgramEntry> BuildPrograms(Channel channel)
    {
        if (string.Equals(channel.Id, PopularChannelId, StringComparison.Ordinal))
        {
            return ResolvePopularPrograms(channel);
        }

        // Rating limits: a channel with time-of-day blocks defers rating to the daypart schedule (so the pool holds
        // every rating and the schedule picks per window); otherwise the all-day band is applied here at build time.
        var ratingBlocks = ResolveRatingBlocks(channel);
        var ratings = HasTimeOfDayRating(ratingBlocks)
            ? new RatingFilter(null, null, true)
            : EffectiveSingleBandFilter(ratingBlocks);
        var kidsScore = ResolveRatingScore(channel.KidsRatingThreshold);
        var years = new YearFilter(channel.Years);
        var kinds = channel.IncludeHomeVideos ? PlayableKindsWithHomeVideos : PlayableKinds;

        var byId = new Dictionary<Guid, BaseItem>();
        var libraryIds = new List<Guid>();
        foreach (var source in channel.Sources)
        {
            if (source.Kind == SourceKind.Collection)
            {
                foreach (var item in CollectionItems(source, ratings, kinds))
                {
                    byId[item.Id] = item;
                }

                continue;
            }

            if (string.IsNullOrEmpty(source.LibraryId) || !Guid.TryParse(source.LibraryId, out var libraryId))
            {
                continue;
            }

            libraryIds.Add(libraryId);
            foreach (var item in ResolveSource(source, libraryId, ratings, kinds))
            {
                byId[item.Id] = item;
            }
        }

        // Additional channel-wide filters, resolved once. Studios match the item or its series (so a network on the
        // series carries to its episodes); people are resolved to the set of items they appear in via one query per
        // library; the rating floors and year set are simple per-item checks. Studios and people are library-scoped,
        // but a collection source can pull from any library, so when one is present these resolve across every
        // library (an empty scope) so collection items are covered the same as library items.
        var hasCollection = channel.Sources.Any(s => s.Kind == SourceKind.Collection);
        var filterScope = hasCollection ? new List<Guid>() : libraryIds;
        var studios = BuildStudioSet(channel.Studios);
        var seriesStudios = studios.Count > 0 ? BuildSeriesStudioMap(filterScope) : new Dictionary<Guid, HashSet<string>>();
        var peopleAllowed = channel.People.Any(p => p.Id != Guid.Empty)
            ? ResolvePeopleAllowed(channel.People, filterScope, kinds)
            : null;

        var entries = new List<ProgramEntry>();
        foreach (var item in byId.Values)
        {
            if (!channel.IncludeSpecials && item is Episode ep && ep.ParentIndexNumber == 0)
            {
                continue;
            }

            // Cheap per-item gates first, so a filtered item never pays the media read below. Year, rating floors,
            // studios, and (when active) the people set each narrow the channel independently.
            if (!years.Allows(item.ProductionYear)
                || !PassesMinRating(item.CommunityRating, channel.MinCommunityRating)
                || !PassesMinRating(item.CriticRating, channel.MinCriticRating)
                || !PassesStudios(studios, EffectiveStudios(item, seriesStudios))
                || (peopleAllowed is not null && !peopleAllowed.Contains(item.Id)))
            {
                continue;
            }

            // Read the item's media streams once and reuse them for both the audio-language filter and the entry's
            // probed metadata, so the whole channel is probed with a single query per item -- here, off the tune-in
            // path -- instead of repeatedly at playback.
            var streams = SafeGetMediaStreams(item.Id);
            if (!AudioLanguageAllows(channel.AudioLanguage, streams))
            {
                continue;
            }

            var entry = ToEntry(item, kidsScore, streams);
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
            channel.Id,
            channel.FavorKind,
            channel.FavorStrength,
            LoopRotation());

        return ProgramLoopBuilder.Build(entries, options);
    }

    // A rotation counter (days since the Unix epoch) that advances which single block each series contributes to a
    // shuffled loop. It is captured into the cached schedule when the schedule is built, so the guide and the live
    // stream always agree, and it advances day over day so a channel works through each series across refreshes.
    private static int LoopRotation() => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    // Resolves one library source to its matching items (before specials/ordering are applied). The
    // selection mode picks exactly one narrowing: all content, a genre filter, a whitelist, or a blacklist.
    private IEnumerable<BaseItem> ResolveSource(LibrarySource source, Guid libraryId, RatingFilter ratings, BaseItemKind[] kinds)
        => source.Selection switch
        {
            SelectionMode.Genre => GenreItems(libraryId, source, ratings, kinds),
            SelectionMode.Whitelist => WhitelistItems(source, ratings, kinds),
            SelectionMode.Blacklist => BlacklistItems(libraryId, source, ratings, kinds),
            _ => QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds)
        };

    // The library narrowed by genre, matched against each item's own genres and, for an episode, its series'
    // genres too (so a series-level tag like "Anime" matches its episodes even when the episodes are untagged).
    // Included genres match any (OR) or every (AND) genre; excluded genres drop any item that carries one. With
    // no genres at all this is the whole library.
    private IEnumerable<BaseItem> GenreItems(Guid libraryId, LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var include = source.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        var exclude = source.ExcludeGenres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        if (include.Length == 0 && exclude.Length == 0)
        {
            return QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds);
        }

        // Series genres apply to their episodes, so build a seriesId -> genres lookup once and use it to compute
        // each item's effective genres below.
        var seriesGenres = SeriesGenreMap(libraryId);

        // Candidates: the whole library when only excluding, else items whose own genres match (database-filtered)
        // unioned with the episodes of series whose genres match (so a series-level tag is honoured).
        IEnumerable<BaseItem> items;
        if (include.Length == 0)
        {
            items = QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds);
        }
        else
        {
            var direct = QueryLibrary(libraryId, include, ratings, kinds);
            var viaSeries = EpisodesOfSeries(SeriesMatching(libraryId, include))
                .Where(e => KindAllowed(e, kinds) && ratings.Allows(e));
            items = direct.Concat(viaSeries).DistinctBy(i => i.Id);
        }

        // Refine the OR-union candidates by the requested match mode, then drop anything carrying an excluded
        // genre. Both checks run against effective (own + series) genres.
        return items.Where(i =>
        {
            var eff = EffectiveGenres(i, seriesGenres);
            var includeOk = include.Length == 0
                || (source.MatchAllGenres ? include.All(eff.Contains) : include.Any(eff.Contains));
            var excludeOk = exclude.Length == 0 || !exclude.Any(eff.Contains);
            return includeOk && excludeOk;
        });
    }

    // A seriesId -> genres lookup for every series in the library, so episode genre matching can include the
    // parent series' genres. One indexed query, far smaller than enumerating episodes.
    private Dictionary<Guid, HashSet<string>> SeriesGenreMap(Guid libraryId)
    {
        var map = new Dictionary<Guid, HashSet<string>>();
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            AncestorIds = new[] { libraryId },
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var s in series)
        {
            map[s.Id] = new HashSet<string>(s.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    // The ids of series in the library carrying any of the genres (database-filtered: one indexed query).
    private List<Guid> SeriesMatching(Guid libraryId, string[] genres)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            AncestorIds = new[] { libraryId },
            Genres = genres,
            Recursive = true,
            IsVirtualItem = false
        }).Select(s => s.Id).ToList();

    // An item's effective genres for matching: its own, plus its series' when it is an episode.
    private static HashSet<string> EffectiveGenres(BaseItem item, Dictionary<Guid, HashSet<string>> seriesGenres)
    {
        var set = new HashSet<string>(item.Genres ?? (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (item is Episode ep && ep.SeriesId != Guid.Empty && seriesGenres.TryGetValue(ep.SeriesId, out var sg))
        {
            set.UnionWith(sg);
        }

        return set;
    }

    // The explicitly chosen shows and movies (series expand to their episodes), kept to playable kinds
    // within the rating cap.
    private IEnumerable<BaseItem> WhitelistItems(LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var result = new List<BaseItem>();
        foreach (var id in new HashSet<Guid>(source.ItemIds))
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            if (item is Series)
            {
                result.AddRange(EpisodesOf(id));
            }
            else
            {
                result.Add(item);
            }
        }

        return result.Where(i => KindAllowed(i, kinds) && ratings.Allows(i));
    }

    // The members of a collection (box set), expanding a member series to its episodes, kept to playable kinds
    // within the rating cap. Collections can span libraries, so this resolves the collection's linked children
    // rather than issuing a library query.
    private IEnumerable<BaseItem> CollectionItems(LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        if (!Guid.TryParse(source.CollectionId, out var id) || _libraryManager.GetItemById(id) is not BoxSet set)
        {
            return Array.Empty<BaseItem>();
        }

        var result = new List<BaseItem>();
        foreach (var member in set.GetLinkedChildren())
        {
            if (member is Series)
            {
                result.AddRange(EpisodesOf(member.Id));
            }
            else
            {
                result.Add(member);
            }
        }

        return result.Where(i => KindAllowed(i, kinds) && ratings.Allows(i));
    }

    // Everything in the library except the chosen shows/movies and their episodes.
    private IEnumerable<BaseItem> BlacklistItems(Guid libraryId, LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var chosen = new HashSet<Guid>(source.ItemIds);
        return QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds).Where(i =>
            !chosen.Contains(i.Id) &&
            !(i is Episode ep && ep.SeriesId != Guid.Empty && chosen.Contains(ep.SeriesId)));
    }

    private static bool KindAllowed(BaseItem item, BaseItemKind[] kinds)
        => Array.IndexOf(kinds, item.GetBaseItemKind()) >= 0;

    private IReadOnlyList<BaseItem> EpisodesOf(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { seriesId },
            Recursive = true,
            IsVirtualItem = false
        });

    // The episodes of every matching series in one batched query (AncestorIds matches descendants of any listed
    // series), instead of a GetItemList call PER series. A genre matching hundreds of series otherwise ran hundreds
    // of sequential queries and stalled a large channel's start-up for ~40s -- past the live-playlist deadline, so
    // the stream handed Jellyfin an empty playlist and the client reported "Failed to load". Chunked so a genre
    // matching very many series cannot overflow the query's host-parameter limit.
    private IReadOnlyList<BaseItem> EpisodesOfSeries(List<Guid> seriesIds)
    {
        if (seriesIds.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        const int batchSize = 200;
        var episodes = new List<BaseItem>();
        for (var start = 0; start < seriesIds.Count; start += batchSize)
        {
            var ancestors = seriesIds.Skip(start).Take(batchSize).ToArray();
            episodes.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = ancestors,
                Recursive = true,
                IsVirtualItem = false
            }));
        }

        return episodes;
    }

    private List<BaseItem> QueryLibrary(Guid libraryId, string[] genres, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            AncestorIds = new[] { libraryId },
            Genres = genres,
            Recursive = true,
            IsVirtualItem = false
        });

        return items.Where(ratings.Allows).ToList();
    }

    // The set of item ids the channel's people appear in, resolved with one PersonIds query per library (all
    // libraries when none are listed, for the Popular channel). An item passes the people filter when it is in this
    // set. Built only when a people filter is active; an empty people list returns an empty set.
    private HashSet<Guid> ResolvePeopleAllowed(IEnumerable<PersonRef> people, IReadOnlyCollection<Guid> libraryIds, BaseItemKind[] kinds)
    {
        var allowed = new HashSet<Guid>();
        var personIds = people.Where(p => p.Id != Guid.Empty).Select(p => p.Id).Distinct().ToArray();
        if (personIds.Length == 0)
        {
            return allowed;
        }

        void Add(InternalItemsQuery query)
        {
            foreach (var item in _libraryManager.GetItemList(query))
            {
                allowed.Add(item.Id);
            }
        }

        if (libraryIds.Count == 0)
        {
            Add(new InternalItemsQuery { PersonIds = personIds, IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false });
        }
        else
        {
            foreach (var libraryId in libraryIds)
            {
                Add(new InternalItemsQuery { PersonIds = personIds, AncestorIds = new[] { libraryId }, IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false });
            }
        }

        return allowed;
    }

    private ProgramEntry? ToEntry(BaseItem? item, int? kidsScore, IReadOnlyList<MediaStream> streams)
    {
        if (item is null)
        {
            return null;
        }

        // Prefer an observed real duration over the metadata runtime; drifted metadata otherwise puts a
        // timestamp gap or overlap at this item's seam every loop.
        var metadataTicks = item.RunTimeTicks ?? 0;
        var ticks = ObservedDurationTicks(item.Id, metadataTicks) ?? metadataTicks;
        if (ticks <= 0 || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var rawName = string.IsNullOrWhiteSpace(item.Name) ? "Untitled" : item.Name;
        var asEpisode = item as Episode;
        var seriesName = asEpisode?.SeriesName;
        var title = !string.IsNullOrWhiteSpace(seriesName) ? seriesName + " - " + rawName : rawName;
        var seriesId = asEpisode is not null && asEpisode.SeriesId != Guid.Empty ? asEpisode.SeriesId : (Guid?)null;

        // Kids when the item carries a rating that ranks at or below the channel's threshold. Movie straight
        // from the item kind, so a movie library tags its programs as movies in the guide.
        var ratingValue = item.InheritedParentalRatingValue;
        var isKids = kidsScore is not null && ratingValue is not null && ratingValue.Value <= kidsScore.Value;

        // Probe the media metadata the live stream needs to choose its decode pipeline and burn-in track, once,
        // here at refresh, from the streams already read. The stream pipeline reads these off the cached entry and
        // never queries the media streams itself.
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (IsDolbyVisionProfile5(video))
        {
            _logger.LogWarning(
                "Live Channels: excluding \"{Title}\": Dolby Vision Profile 5 has no HDR10-compatible base layer, so every tone mapper renders it with wrong (green/purple) colours. A Profile 8 or HDR10 version of it will play correctly.",
                title);
            return null;
        }

        var defaultAudio = DefaultAudio(streams);

        return new ProgramEntry(item.Id, title, item.Overview, ticks, item.Path)
        {
            Year = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            ParentalRatingValue = item.InheritedParentalRatingValue,
            Genres = item.Genres ?? Array.Empty<string>(),
            SeasonNumber = asEpisode?.ParentIndexNumber,
            EpisodeNumber = asEpisode?.IndexNumber,
            IsMovie = item.GetBaseItemKind() == BaseItemKind.Movie,
            IsKids = isKids,
            SeriesId = seriesId,
            SeriesName = seriesName,
            RawName = rawName,
            GuideImagePath = ResolveGuideImage(item),
            SourceHeight = item.Height,
            DateAdded = item.DateCreated,
            CommunityRating = item.CommunityRating,
            PremiereDate = item.PremiereDate,
            IsHdr = ComputeIsHdr(video),
            DefaultAudioOrdinal = defaultAudio?.Ordinal ?? 0,
            DefaultAudioLanguage = defaultAudio?.Stream.Language,
            Subtitles = BuildSubtitleInfos(streams)
        };
    }

    // Picks landscape-friendly guide artwork: a movie's backdrop, otherwise the primary image (episode and
    // music-video primaries are already landscape thumbnails). Falls back to the other type so a program still
    // shows something when its preferred art is missing.
    private static string? ResolveGuideImage(BaseItem item)
    {
        var isMovie = item.GetBaseItemKind() == BaseItemKind.Movie;
        var preferred = isMovie ? ImageType.Backdrop : ImageType.Primary;
        var fallback = isMovie ? ImageType.Primary : ImageType.Backdrop;

        if (item.HasImage(preferred))
        {
            return item.GetImagePath(preferred, 0);
        }

        if (item.HasImage(fallback))
        {
            return item.GetImagePath(fallback, 0);
        }

        return null;
    }
}
