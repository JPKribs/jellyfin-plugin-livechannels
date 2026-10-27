using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
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
    // The item kinds a channel includes, from its Content Types toggles. Episodes are queried when either regular
    // episodes or specials are wanted; the season-0 split between them is applied per item during the build.
    private static BaseItemKind[] BuildKinds(Channel channel)
    {
        var kinds = new List<BaseItemKind>(4);
        if (channel.IncludeMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (channel.IncludeEpisodes || channel.IncludeSpecials)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (channel.IncludeMusicVideos)
        {
            kinds.Add(BaseItemKind.MusicVideo);
        }

        if (channel.IncludeHomeVideos)
        {
            kinds.Add(BaseItemKind.Video);
        }

        return kinds.ToArray();
    }

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

    // ISO 639-2 bibliographic codes and their terminology twins (ger/deu, fre/fra, ...). Media is tagged with
    // either, and .NET's cultures only know the terminology form.
    private static readonly Dictionary<string, string> BibliographicLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld",
        ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri",
        ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    /// <summary>
    /// The audio track a channel plays for an item, and its ordinal among the item's audio streams: the default
    /// track in the preferred language, else the first track in that language, else the default track, else the
    /// first track. <c>null</c> when the item has no audio.
    /// </summary>
    /// <param name="streams">The item's media streams.</param>
    /// <param name="preferredLanguage">The language to prefer (ISO 639 code), or empty for none.</param>
    /// <returns>The chosen track and its ordinal, or <c>null</c>.</returns>
    internal static (int Ordinal, MediaStream Stream)? ChooseAudio(IReadOnlyList<MediaStream> streams, string? preferredLanguage)
    {
        // Only tracks inside the item's own file: an external audio file (a sidecar dub) is listed among the
        // item's streams too, but the producer maps audio from the main file alone, so choosing one would map
        // nothing and play the item silent.
        var audio = streams.Where(IsEmbeddedAudio).OrderBy(s => s.Index).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        int Pick(Func<MediaStream, bool> match) => audio.FindIndex(s => match(s));
        var ordinal = -1;
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            ordinal = Pick(s => s.IsDefault && LanguageMatches(s.Language, preferredLanguage));
            if (ordinal < 0)
            {
                ordinal = Pick(s => LanguageMatches(s.Language, preferredLanguage));
            }
        }

        if (ordinal < 0)
        {
            ordinal = Pick(s => s.IsDefault);
        }

        if (ordinal < 0)
        {
            ordinal = 0;
        }

        return (ordinal, audio[ordinal]);
    }

    /// <summary>
    /// Whether two language tags name the same language, across ISO 639-1 (<c>en</c>), 639-2 terminology
    /// (<c>eng</c>, <c>deu</c>), and 639-2 bibliographic (<c>ger</c>) forms. Region suffixes are ignored.
    /// </summary>
    /// <param name="a">One tag.</param>
    /// <param name="b">The other tag.</param>
    /// <returns>Whether they match.</returns>
    internal static bool LanguageMatches(string? a, string? b)
    {
        var x = NormalizeLanguage(a);
        var y = NormalizeLanguage(b);
        return x.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a language tag names an actual language. Empty, and the ISO 639-2 codes that say there is none to
    /// name (<c>und</c> undetermined, which ffprobe reports for untagged tracks; <c>zxx</c> no linguistic content;
    /// <c>mul</c> multiple; <c>mis</c> uncoded), do not.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it names a language.</returns>
    internal static bool IsTaggedLanguage(string? tag) => NormalizeLanguage(tag).Length > 0;

    // A language tag as an ISO 639-2 terminology code where one is known, else the tag itself, lower case. Empty
    // for a tag that names no language (see IsTaggedLanguage), so such a tag never matches anything.
    private static string NormalizeLanguage(string? tag)
    {
        var code = (tag ?? string.Empty).Trim().Split('-', '_')[0].ToLowerInvariant();
        if (code.Length == 0 || code is "und" or "zxx" or "mul" or "mis" or "unknown")
        {
            return string.Empty;
        }

        if (BibliographicLanguages.TryGetValue(code, out var terminology))
        {
            return terminology;
        }

        if (code.Length == 2)
        {
            try
            {
                var three = CultureInfo.GetCultureInfo(code).ThreeLetterISOLanguageName;
                return string.IsNullOrEmpty(three) ? code : three.ToLowerInvariant();
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        return code;
    }

    // The language a channel prefers its audio in: the channel's own audio language when set, else the plugin's
    // default language (the viewer's native language).
    private static string PreferredAudioLanguage(Channel channel)
        => !string.IsNullOrWhiteSpace(channel.AudioLanguage)
            ? channel.AudioLanguage
            : Plugin.Instance?.ReadConfiguration(c => c.DefaultSubtitleLanguage) ?? string.Empty;

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
                IsText = s.IsTextSubtitleStream,
                IsExternal = s.IsExternal,
                Language = s.Language
            };
        }

        return infos;
    }

    /// <summary>A display rotation as 0, 90, 180 or 270 degrees (negative and over-full turns folded in).</summary>
    /// <param name="degrees">The rotation Jellyfin probed, or <c>null</c>.</param>
    /// <returns>The normalised rotation.</returns>
    internal static int NormalizeRotation(int? degrees)
        => degrees is { } d ? (((d % 360) + 360) % 360 + 45) / 90 % 4 * 90 : 0;

    // An audio track inside the item's own file, the only kind the producer can map.
    private static bool IsEmbeddedAudio(MediaStream stream) => stream.Type == MediaStreamType.Audio && !stream.IsExternal;

    // Whether an item has audio in the channel's required language (an ISO 639 code): the track it would play,
    // chosen with that language preferred (see ChooseAudio), is in it. An empty language allows everything. An item
    // with no track in the language, untagged audio, or unreadable streams is excluded.
    private static bool AudioLanguageAllows(string language, IReadOnlyList<MediaStream> streams)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var chosen = ChooseAudio(streams, language);
        return chosen is not null && LanguageMatches(chosen.Value.Stream.Language, language);
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
        var years = new YearFilter(channel.Years);
        var kinds = BuildKinds(channel);
        if (kinds.Length == 0)
        {
            // No content types are enabled, so nothing can air. Return empty rather than risk an unfiltered query.
            return Array.Empty<ProgramEntry>();
        }

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

        // Series artwork memoised for this one build, so a series contributing dozens of episodes is loaded once
        // rather than per episode. Local to the build, so concurrent builds (guide refresh, a tune-in rebuild, a DVR
        // resolve) never clear each other's memo, and a metadata refresh is picked up by the next build.
        var seriesArtwork = new Dictionary<Guid, ArtworkSet>();
        var entries = new List<ProgramEntry>();
        foreach (var item in byId.Values)
        {
            if (item is Episode ep)
            {
                var special = ep.ParentIndexNumber == 0;
                if (special ? !channel.IncludeSpecials : !channel.IncludeEpisodes)
                {
                    continue;
                }
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

            var entry = ToEntry(item, streams, seriesArtwork, PreferredAudioLanguage(channel));
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

    private ProgramEntry? ToEntry(BaseItem? item, IReadOnlyList<MediaStream> streams, Dictionary<Guid, ArtworkSet> seriesArtwork, string preferredLanguage)
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

        var defaultAudio = ChooseAudio(streams, preferredLanguage);
        var images = ResolveGuideImages(item, seriesArtwork);

        return new ProgramEntry(item.Id, title, item.Overview, ticks, item.Path)
        {
            Year = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            SeriesOfficialRating = asEpisode?.Series?.OfficialRating,
            Tagline = string.IsNullOrWhiteSpace(item.Tagline) ? null : item.Tagline.Trim(),
            Artists = (item as MusicVideo)?.Artists ?? Array.Empty<string>(),
            ItemKind = item.GetBaseItemKind().ToString(),
            ParentalRatingValue = item.InheritedParentalRatingValue,
            Genres = item.Genres ?? Array.Empty<string>(),
            SeasonNumber = asEpisode?.ParentIndexNumber,
            EpisodeNumber = asEpisode?.IndexNumber,
            IsMovie = item.GetBaseItemKind() == BaseItemKind.Movie,
            SeriesId = seriesId,
            SeriesName = seriesName,
            RawName = rawName,
            PrimaryImagePath = images.Primary,
            ThumbImagePath = images.Thumb,
            BackdropImagePath = images.Backdrop,
            LogoImagePath = images.Logo,
            SourceHeight = item.Height,
            DateAdded = item.DateCreated,
            CommunityRating = item.CommunityRating,
            PremiereDate = item.PremiereDate,
            IsHdr = ComputeIsHdr(video),
            DefaultAudioOrdinal = defaultAudio?.Ordinal ?? 0,
            IsVideoOnly = video is not null && !streams.Any(IsEmbeddedAudio),
            SourceVideoCodec = video?.Codec,
            SourceBitDepth = video?.BitDepth,
            SourcePixelFormat = video?.PixelFormat,
            SourceColorSpace = video?.ColorSpace,
            SourceRotation = NormalizeRotation(video?.Rotation),
            SourceAudioCodec = defaultAudio?.Stream.Codec,
            SourceAudioChannels = defaultAudio?.Stream.Channels,
            DefaultAudioLanguage = defaultAudio?.Stream.Language,
            Subtitles = BuildSubtitleInfos(streams)
        };
    }

    // Every image an item carries, so the guide can offer a client both a portrait poster and a landscape thumb
    // (plus a backdrop and a clear logo) instead of one image forced into whichever shape the client lays out.
    // An episode's own primary is a landscape still, so its poster comes from the series, resolved once per
    // series and memoised for the rest of the build.
    private ArtworkSet ResolveGuideImages(BaseItem item, Dictionary<Guid, ArtworkSet> seriesArtwork)
    {
        var own = ArtworkOf(item);
        var kind = item.GetBaseItemKind();
        var landscapePrimary = kind is BaseItemKind.Episode or BaseItemKind.MusicVideo;
        var parent = item is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty
            ? ArtworkOfSeries(seriesId, seriesArtwork)
            : default;

        return GuideImages.Select(own, parent, landscapePrimary);
    }

    // A series' artwork, resolved once per series per build. A series that cannot be loaded caches as "no
    // artwork", so a broken link is not re-queried for every one of its episodes.
    private ArtworkSet ArtworkOfSeries(Guid seriesId, Dictionary<Guid, ArtworkSet> seriesArtwork)
    {
        if (seriesArtwork.TryGetValue(seriesId, out var cached))
        {
            return cached;
        }

        var series = _libraryManager.GetItemById(seriesId);
        var artwork = series is null ? default : ArtworkOf(series);
        seriesArtwork[seriesId] = artwork;
        return artwork;
    }

    private static ArtworkSet ArtworkOf(BaseItem item)
        => new(
            ImagePathOrNull(item, ImageType.Primary),
            ImagePathOrNull(item, ImageType.Thumb),
            ImagePathOrNull(item, ImageType.Backdrop),
            ImagePathOrNull(item, ImageType.Logo));

    private static string? ImagePathOrNull(BaseItem item, ImageType type)
        => item.HasImage(type) ? item.GetImagePath(type, 0) : null;
}
