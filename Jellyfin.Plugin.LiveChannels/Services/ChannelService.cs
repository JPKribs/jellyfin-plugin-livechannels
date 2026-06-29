using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Resolves configured channels into the ordered item loops their schedule and live streams are built from.
/// </summary>
public class ChannelService
{
    private static readonly BaseItemKind[] PlayableKinds = { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<ChannelService> _logger;

    // Item/track keys whose subtitle extraction is currently running, so concurrent tune-ins don't pile a
    // second whole-file extraction onto the producer's critical path.
    private readonly ConcurrentDictionary<string, byte> _subtitleExtractions = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager used to resolve items.</param>
    /// <param name="mediaSourceManager">The media source manager used to inspect subtitle streams.</param>
    /// <param name="subtitleEncoder">The subtitle encoder used to extract (and cache) embedded subtitles for burn-in.</param>
    /// <param name="localization">The localization manager used to rank official ratings.</param>
    /// <param name="logger">The logger.</param>
    public ChannelService(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISubtitleEncoder subtitleEncoder, ILocalizationManager localization, ILogger<ChannelService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _localization = localization;
        _logger = logger;
    }

    /// <summary>
    /// Whether the item's video is HDR (PQ or HLG), so the stream pipeline can tone-map it to SDR. Keyed off the
    /// colour transfer like every mature pseudo-TV pipeline does (ErsatzTV/Tunarr): <c>smpte2084</c> = HDR10/PQ,
    /// <c>arib-std-b67</c> = HLG.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item's video stream carries an HDR transfer function.</returns>
    public bool IsHdrSource(Guid itemId)
    {
        try
        {
            var transfer = _mediaSourceManager.GetMediaStreams(itemId)
                .FirstOrDefault(s => s.Type == MediaStreamType.Video)?.ColorTransfer;
            return string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for HDR check on {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// The position, among an item's audio streams ordered by index, of the track Jellyfin marks as default, or
    /// 0 when none is flagged, so the stream pipeline maps the same audio track Jellyfin itself would play
    /// instead of letting ffmpeg pick by channel count. Returns <c>null</c> when the streams cannot be read.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The default audio track's ordinal among the item's audio streams, or <c>null</c>.</returns>
    public int? GetDefaultAudioOrdinal(Guid itemId)
    {
        try
        {
            return DefaultAudio(_mediaSourceManager.GetMediaStreams(itemId))?.Ordinal ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for audio selection on {ItemId}", itemId);
            return null;
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

    // Whether the item's default audio track is in the channel's required language (a three-letter ISO code).
    // An empty language allows everything and skips the lookup entirely, so only language-filtered channels pay
    // the per-item media-stream read. Strict by design ("MUST be this language"): an item whose default track is
    // another language, is untagged, or cannot be read is excluded.
    private bool AudioLanguageAllows(string language, Guid itemId)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        try
        {
            var def = DefaultAudio(_mediaSourceManager.GetMediaStreams(itemId));
            return def is not null && string.Equals(def.Value.Stream.Language, language, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for audio language check on {ItemId}", itemId);
            return false;
        }
    }

    // ISO 639 English codes, the only language treated as needing no subtitles for non-English-audio burn-in.
    private static bool IsEnglish(string? language)
        => string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase)
        || string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the subtitle track to burn into an item for the given mode. <see cref="SubtitleBurnInMode.Forced"/>
    /// burns only the forced track, except when the default audio track is in a known non-English language, where
    /// it behaves like <see cref="SubtitleBurnInMode.Always"/> so foreign-language content stays followable.
    /// <see cref="SubtitleBurnInMode.Always"/> burns the forced track when present, otherwise the default or first.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="mode">The channel's subtitle burn-in mode.</param>
    /// <returns>The chosen subtitle's index among the item's subtitle streams and whether it is text-based, or <c>null</c> when nothing should be burned in.</returns>
    public (int RelativeIndex, bool IsText)? FindBurnInSubtitle(Guid itemId, SubtitleBurnInMode mode)
    {
        if (mode == SubtitleBurnInMode.Never)
        {
            return null;
        }

        IReadOnlyList<MediaStream> streams;
        try
        {
            streams = _mediaSourceManager.GetMediaStreams(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for {ItemId}", itemId);
            return null;
        }

        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();

        // Forced always wins when present, in either mode.
        for (var i = 0; i < subtitles.Count; i++)
        {
            if (subtitles[i].IsForced)
            {
                return (i, subtitles[i].IsTextSubtitleStream);
            }
        }

        // "Forced only" also burns subtitles when the audio we play is in a known non-English language, so
        // foreign-language content stays followable. English or untagged audio shows nothing without a forced
        // track, so ordinary English content is never subtitled by surprise.
        var audioLanguage = DefaultAudio(streams)?.Stream.Language;
        var forcedForNonEnglishAudio = mode == SubtitleBurnInMode.Forced
            && !string.IsNullOrEmpty(audioLanguage)
            && !IsEnglish(audioLanguage);

        if ((mode == SubtitleBurnInMode.Always || forcedForNonEnglishAudio) && subtitles.Count > 0)
        {
            // Both "Always" and non-English-audio "Forced only" burn the same track "Always" picks: the default
            // subtitle, or the first one when none is flagged default.
            var defaultIndex = subtitles.FindIndex(s => s.IsDefault);
            var chosen = defaultIndex >= 0 ? defaultIndex : 0;
            return (chosen, subtitles[chosen].IsTextSubtitleStream);
        }

        return null;
    }

    /// <summary>
    /// Extracts the chosen embedded text subtitle to an ASS file (extracted once and cached by Jellyfin) and
    /// returns its path, so a burn-in tune-in can read a tiny subtitle file instead of scanning the whole media
    /// file from the start. Bounded by a short timeout: a cold extraction keeps running in the background to warm
    /// the cache while the caller falls back to no subtitle for that one tune-in.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="relativeIndex">The chosen subtitle's index among the item's subtitle streams.</param>
    /// <param name="offset">How far into the item the tune-in is; only events from here on are kept.</param>
    /// <param name="outputDirectory">Where to write the burn-ready ASS file.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ASS file path, or <c>null</c> when it could not be produced in time.</returns>
    public async Task<string?> TryExtractTuneInSubtitleAsync(Guid itemId, int relativeIndex, TimeSpan offset, string outputDirectory, CancellationToken cancellationToken)
    {
        var key = itemId.ToString("N", CultureInfo.InvariantCulture) + "-" + relativeIndex.ToString(CultureInfo.InvariantCulture);

        // Only one extraction per item/track at a time. A concurrent tune-in skips rather than launching a
        // duplicate; it simply starts without a burned-in subtitle, as a cold tune-in already does.
        if (!_subtitleExtractions.TryAdd(key, 0))
        {
            return null;
        }

        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return null;
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            var source = sources.Count > 0 ? sources[0] : null;
            if (source?.MediaStreams is null)
            {
                return null;
            }

            var subtitles = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
            if (relativeIndex < 0 || relativeIndex >= subtitles.Count)
            {
                return null;
            }

            var absoluteIndex = subtitles[relativeIndex].Index;

            // GetSubtitles extracts the embedded subtitle to ASS (reading the file once, then caching it) and
            // filters it to the events from the tune-in point on, keeping their original timestamps so they line
            // up with the -copyts video. CancellationToken.None so a cold extraction still finishes caching even
            // when we stop waiting for it below.
            //
            // Wait only briefly: this runs on the producer's critical path, so a long wait delays the first frame
            // and the player gives up. A cached subtitle parses well within this; an uncold one keeps extracting
            // in the background and is used on the next tune-in, while this one starts immediately without it.
            var extract = _subtitleEncoder.GetSubtitles(item, source.Id, absoluteIndex, "ass", offset.Ticks, 0, true, CancellationToken.None);
            var ready = await Task.WhenAny(extract, Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken)).ConfigureAwait(false);
            if (ready != extract || extract.Status != TaskStatus.RanToCompletion)
            {
                return null;
            }

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "lc-sub-" + key + ".ass");
            var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";

            var subtitleStream = await extract.ConfigureAwait(false);
            var file = new FileStream(temp, FileMode.Create, FileAccess.Write);
            try
            {
                await using (subtitleStream.ConfigureAwait(false))
                await using (file.ConfigureAwait(false))
                {
                    await subtitleStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                // Publish atomically so a reader (libass) or a concurrent writer never sees a half-written ASS.
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDeleteSubtitle(temp);
                throw;
            }

            return path;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not prepare a tune-in subtitle for {ItemId}", itemId);
            return null;
        }
        finally
        {
            _subtitleExtractions.TryRemove(key, out _);
        }
    }

    private void TryDeleteSubtitle(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete temp subtitle {Path}", path);
        }
    }

    /// <summary>
    /// Returns the enabled channels with a usable id, ordered by channel number.
    /// </summary>
    /// <returns>The enabled channels.</returns>
    public IReadOnlyList<Channel> GetEnabledChannels()
    {
        if (Plugin.Instance is null)
        {
            return Array.Empty<Channel>();
        }

        var channels = Plugin.Instance.ReadConfiguration(config => config.Channels
            .Where(c => c.Enabled && !string.IsNullOrEmpty(c.Id))
            .ToList());

        if (PopularChannelEnabled())
        {
            channels.Add(BuildPopularChannel());
        }

        return channels.OrderBy(c => c.Number).ToList();
    }

    /// <summary>
    /// Finds an enabled channel by id.
    /// </summary>
    /// <param name="id">The channel id.</param>
    /// <returns>The channel, or <c>null</c> when no enabled channel matches.</returns>
    public Channel? FindChannel(string? id)
    {
        if (string.IsNullOrEmpty(id) || Plugin.Instance is null)
        {
            return null;
        }

        if (string.Equals(id, PopularChannelId, StringComparison.Ordinal))
        {
            return PopularChannelEnabled() ? BuildPopularChannel() : null;
        }

        return Plugin.Instance.ReadConfiguration(config => config.Channels
            .FirstOrDefault(c => c.Enabled && string.Equals(c.Id, id, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Resolves a channel's items into the ordered, schedulable loop it cycles through. Content is the union
    /// of every library source; items without a playable file or a positive runtime are dropped because they
    /// cannot be placed on the timeline.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> ResolvePrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (string.Equals(channel.Id, PopularChannelId, StringComparison.Ordinal))
        {
            return ResolvePopularPrograms();
        }

        var ratings = new RatingFilter(
            ResolveRatingScore(channel.MinOfficialRating),
            ResolveRatingScore(channel.MaxOfficialRating),
            channel.IncludeUnrated);
        var kidsScore = ResolveRatingScore(channel.KidsRatingThreshold);
        var kinds = PlayableKinds;

        var byId = new Dictionary<Guid, BaseItem>();
        foreach (var source in channel.Sources)
        {
            if (string.IsNullOrEmpty(source.LibraryId) || !Guid.TryParse(source.LibraryId, out var libraryId))
            {
                continue;
            }

            foreach (var item in ResolveSource(source, libraryId, ratings, kinds))
            {
                byId[item.Id] = item;
            }
        }

        var entries = new List<ProgramEntry>();
        foreach (var item in byId.Values)
        {
            if (!channel.IncludeSpecials && item is Episode ep && ep.ParentIndexNumber == 0)
            {
                continue;
            }

            if (!AudioLanguageAllows(channel.AudioLanguage, item.Id))
            {
                continue;
            }

            var entry = ToEntry(item, kidsScore);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var options = new ChannelLoopOptions(
            channel.EpisodesPerBlock,
            channel.KeepMultiPartTogether,
            channel.Shuffle,
            channel.ShuffleEpisodes,
            channel.Id,
            channel.FavorKind,
            channel.FavorStrength);

        return ProgramLoopBuilder.Build(entries, options);
    }

    // ---- Built-in "Popular" channel (channel 0) ----

    /// <summary>The stable id of the built-in Popular channel.</summary>
    public const string PopularChannelId = "livechannels-popular";

    private const int PopularPerKind = 10;

    // Play a run of consecutive episodes of a series before moving on, in air order, so a popular show airs as a
    // coherent block (e.g. episodes 5, 6, 7, 8) rather than scattered single episodes.
    private const int PopularEpisodesPerBlock = 4;

    private static bool PopularChannelEnabled()
        => Plugin.Instance?.ReadConfiguration(c => c.PopularChannelEnabled) ?? false;

    // The synthetic channel definition: number 0, a Material Symbol logo, shuffled. It carries no per-channel
    // filters because its content comes from the popular query rather than configured library sources.
    private static Channel BuildPopularChannel() => new()
    {
        Id = PopularChannelId,
        Name = "Popular",
        Number = 0,
        LogoStyle = LogoStyle.Symbol,
        LogoSymbol = "diversity_1",
        LogoShowName = true,
        Enabled = true,
        Shuffle = true,
        ShuffleEpisodes = false,
        EpisodesPerBlock = PopularEpisodesPerBlock,
        IncludeUnrated = true,
        KidsRatingThreshold = string.Empty
    };

    // Resolves the Popular channel's loop: the most popular movies and shows across the whole library (series
    // expand to their episodes), blended with recent additions so new content still surfaces.
    private IReadOnlyList<ProgramEntry> ResolvePopularPrograms()
    {
        var items = new Dictionary<Guid, BaseItem>();
        foreach (var series in TopPopular(BaseItemKind.Series, PopularPerKind))
        {
            foreach (var episode in EpisodesOf(series.Id))
            {
                items[episode.Id] = episode;
            }
        }

        foreach (var movie in TopPopular(BaseItemKind.Movie, PopularPerKind))
        {
            items[movie.Id] = movie;
        }

        var entries = new List<ProgramEntry>();
        foreach (var item in items.Values)
        {
            if (item is Episode ep && ep.ParentIndexNumber == 0)
            {
                continue; // skip specials
            }

            var entry = ToEntry(item, kidsScore: null);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var options = new ChannelLoopOptions(
            PopularEpisodesPerBlock,
            true,
            true,
            false,
            PopularChannelId,
            FavorKind.None,
            FavorStrength.Moderate);

        return ProgramLoopBuilder.Build(entries, options);
    }

    // The top N items of a kind across every library, ranked by a popularity score that blends community rating
    // with a recency boost. Two candidate pools (highest rated, most recently added) are merged so both
    // well-loved and brand-new content can make the cut.
    private List<BaseItem> TopPopular(BaseItemKind kind, int count)
    {
        var byRating = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            Limit = count * 3
        });

        var byRecency = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            Limit = count
        });

        var now = DateTime.UtcNow;
        return byRating.Concat(byRecency)
            .DistinctBy(i => i.Id)
            .OrderByDescending(i => PopularityScore(i.CommunityRating, i.DateCreated, now))
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Scores an item's "popularity" for the built-in Popular channel: its community rating (0-10) plus a recency
    /// boost that adds up to 3 for a brand-new item and decays to zero over about two years.
    /// </summary>
    /// <param name="communityRating">The item's community rating, or <c>null</c>.</param>
    /// <param name="dateAdded">When the item was added to the library.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The blended score; a higher score ranks first.</returns>
    public static double PopularityScore(float? communityRating, DateTime dateAdded, DateTime now)
    {
        const double RecencyWindowDays = 730.0;
        const double MaxRecencyBoost = 3.0;
        var rating = communityRating ?? 0f;
        var ageDays = Math.Max(0, (now - dateAdded).TotalDays);
        var recency = MaxRecencyBoost * Math.Max(0, 1 - (ageDays / RecencyWindowDays));
        return rating + recency;
    }

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
            var viaSeries = SeriesMatching(libraryId, include)
                .SelectMany(EpisodesOf)
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

    private static ProgramEntry? ToEntry(BaseItem? item, int? kidsScore)
    {
        if (item is null)
        {
            return null;
        }

        var ticks = item.RunTimeTicks ?? 0;
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

        return new ProgramEntry(item.Id, title, item.Overview, ticks, item.Path)
        {
            Year = item.ProductionYear,
            OfficialRating = item.OfficialRating,
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
            PremiereDate = item.PremiereDate
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
