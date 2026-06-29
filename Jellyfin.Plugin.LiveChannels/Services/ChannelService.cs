using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Common.Configuration;
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

    // The same kinds plus loose Video items, which is what Jellyfin types home videos as. Used when a channel
    // opts in to home videos; a Movies/Shows library has no Video-kind items, so this is a no-op for them.
    private static readonly BaseItemKind[] PlayableKindsWithHomeVideos = { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILocalizationManager _localization;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ChannelService> _logger;

    // Item/track keys whose subtitle extraction is currently running, so concurrent tune-ins don't pile a
    // second whole-file extraction onto the producer's critical path.
    private readonly ConcurrentDictionary<string, byte> _subtitleExtractions = new(StringComparer.Ordinal);

    // Memoised HDR result per item: the check is a media-stream query and is asked repeatedly (channel start-up,
    // per-item playback, re-tunes). An item's HDR-ness does not change, so caching it removes thousands of repeat
    // queries on large channels. Bounded by the library size (one bool per item ever checked).
    private readonly ConcurrentDictionary<Guid, bool> _hdrCache = new();

    // Memoised interlaced result per item, for the same reasons as the HDR cache. Used to keep interlaced sources
    // off the Intel hardware-decode path, whose deinterlace + hwdownload chain fails on QSV/VAAPI (exit 234).
    private readonly ConcurrentDictionary<Guid, bool> _interlacedCache = new();

    // Memoised 10-bit result per item. Used to keep 10-bit sources off the Intel hardware-decode path: that path
    // downloads decoded frames with hwdownload,format=nv12, which fails on a 10-bit (p010) surface ("Invalid output
    // format nv12 for hwframe download", exit 234) regardless of whether the source is interlaced or progressive.
    private readonly ConcurrentDictionary<Guid, bool> _tenBitCache = new();

    // Resolved schedules cached on disk so the expensive library resolve runs once -- on guide refresh -- and every
    // tune-in reads the result instead of rebuilding it on the stream's start-up critical path (a large channel's
    // rebuild took ~40s, past the live-playlist deadline, and the client reported "Failed to load"). One file holds
    // every channel's loop, stored in the stream root (default <cache>/livechannels) as schedule.json -- a loose
    // file, not a session directory, so the stream cleanup task (which only removes directories) never deletes it.
    // Cleared when the plugin configuration changes so channel edits take effect on the next guide refresh or tune-in.
    private static readonly JsonSerializerOptions ScheduleCacheJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly object ScheduleLock = new();

    /// <summary>The file name of the persistent schedule cache, kept in the stream root.</summary>
    public const string ScheduleFileName = "schedule.json";

    private readonly string _scheduleFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager used to resolve items.</param>
    /// <param name="mediaSourceManager">The media source manager used to inspect subtitle streams.</param>
    /// <param name="subtitleEncoder">The subtitle encoder used to extract (and cache) embedded subtitles for burn-in.</param>
    /// <param name="localization">The localization manager used to rank official ratings.</param>
    /// <param name="userManager">The user manager, used to read server-wide watch data for the Popular channel.</param>
    /// <param name="userDataManager">The user-data manager, used to read per-item play counts for the Popular channel.</param>
    /// <param name="appPaths">The application paths, used to locate the stream root the schedule cache lives in.</param>
    /// <param name="logger">The logger.</param>
    public ChannelService(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISubtitleEncoder subtitleEncoder, ILocalizationManager localization, IUserManager userManager, IUserDataManager userDataManager, IApplicationPaths appPaths, ILogger<ChannelService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _localization = localization;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _scheduleFile = ScheduleFile(appPaths);
        _logger = logger;
    }

    /// <summary>
    /// Whether the item's video is HDR (PQ or HLG), so the stream pipeline can tone-map it to SDR. Keyed off the
    /// colour transfer like every mature pseudo-TV pipeline does (ErsatzTV/Tunarr): <c>smpte2084</c> = HDR10/PQ,
    /// <c>arib-std-b67</c> = HLG.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item's video stream carries an HDR transfer function.</returns>
    public bool IsHdrSource(Guid itemId) => _hdrCache.GetOrAdd(itemId, ComputeIsHdrSource);

    private bool ComputeIsHdrSource(Guid itemId)
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
    /// Whether the item's video stream is interlaced. Cached, because the per-item pipeline asks once per item and
    /// an item's field type does not change. Interlaced sources are kept off the Intel hardware-decode path, whose
    /// deinterlace step downloads frames to system memory and fails to reconfigure on QSV/VAAPI.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item's video stream is flagged interlaced.</returns>
    public bool IsInterlacedSource(Guid itemId) => _interlacedCache.GetOrAdd(itemId, ComputeIsInterlaced);

    private bool ComputeIsInterlaced(Guid itemId)
    {
        try
        {
            return _mediaSourceManager.GetMediaStreams(itemId)
                .FirstOrDefault(s => s.Type == MediaStreamType.Video)?.IsInterlaced ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for interlace check on {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Whether the item's video stream is 10-bit (or deeper). Cached, like the HDR and interlaced checks. 10-bit
    /// sources are kept off the Intel hardware-decode path, whose <c>hwdownload,format=nv12</c> step fails to
    /// download a 10-bit (p010) GPU surface (exit 234); software-decoding them instead avoids the failure.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item's video stream carries 10 or more bits per sample.</returns>
    public bool Is10BitSource(Guid itemId) => _tenBitCache.GetOrAdd(itemId, Compute10Bit);

    private bool Compute10Bit(Guid itemId)
    {
        try
        {
            var video = _mediaSourceManager.GetMediaStreams(itemId)
                .FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video is null)
            {
                return false;
            }

            // Prefer the explicit bit-depth; fall back to the pixel format name when the probe did not populate
            // BitDepth. Match only true depth suffixes (e.g. yuv420p10le, yuv444p12le, p010le) so common 8-bit
            // formats are NOT misread: a loose Contains("10")/Contains("12") would wrongly match nv12 (the standard
            // 8-bit format, contains "12") and yuv410p (contains "10"), needlessly software-decoding 8-bit content.
            if (video.BitDepth is int depth)
            {
                return depth >= 10;
            }

            var pix = video.PixelFormat;
            return !string.IsNullOrEmpty(pix)
                && (pix.Contains("10le", StringComparison.Ordinal) || pix.Contains("10be", StringComparison.Ordinal)
                    || pix.Contains("12le", StringComparison.Ordinal) || pix.Contains("12be", StringComparison.Ordinal)
                    || pix.Contains("p010", StringComparison.OrdinalIgnoreCase) || pix.Contains("p012", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for bit-depth check on {ItemId}", itemId);
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

        // "Forced only" also burns subtitles when the audio we play is not in the configured default language, so
        // foreign-language content stays followable. Audio in the default language, or untagged audio, shows
        // nothing without a forced track, so ordinary content is never subtitled by surprise.
        var defaultLanguage = Plugin.Instance?.ReadConfiguration(c => c.DefaultSubtitleLanguage) ?? string.Empty;
        var audioLanguage = DefaultAudio(streams)?.Stream.Language;
        var forcedForForeignAudio = mode == SubtitleBurnInMode.Forced
            && !string.IsNullOrEmpty(audioLanguage)
            && !string.IsNullOrEmpty(defaultLanguage)
            && !string.Equals(audioLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase);

        if ((mode == SubtitleBurnInMode.Always || forcedForForeignAudio) && subtitles.Count > 0)
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
    /// Returns a channel's ordered program loop, reading the on-disk cache the guide refresh writes so a tune-in
    /// never pays the full library resolve. Builds (and caches) it only when no cache exists yet.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> ResolvePrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return TryReadScheduleCache(channel) ?? RefreshPrograms(channel);
    }

    /// <summary>
    /// Builds a channel's program loop and overwrites its on-disk cache. Called on guide refresh so the schedule
    /// is built once and every tune-in until the next refresh reuses it instead of re-resolving the library.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The freshly built, ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> RefreshPrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var programs = BuildPrograms(channel);
        WriteScheduleCache(channel, programs);
        return programs;
    }

    /// <summary>
    /// Deletes the cached schedule. Called when the plugin configuration changes so a channel edit is never served
    /// from a stale cache; the next guide refresh (or tune-in) rebuilds it.
    /// </summary>
    /// <param name="paths">The application paths, used to locate the stream root the cache lives in.</param>
    public static void ClearScheduleCache(IApplicationPaths paths)
    {
        try
        {
            lock (ScheduleLock)
            {
                var file = ScheduleFile(paths);
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Best effort: a stale cache is overwritten on the next guide refresh regardless.
        }
    }

    /// <summary>
    /// Resolves the directory the live streams (and the schedule cache) are written to: the configured stream
    /// directory, or a livechannels folder under Jellyfin's cache by default. Shared with the live-TV service so
    /// both agree on where schedule.json lives.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The stream root directory.</returns>
    public static string ResolveStreamRoot(IApplicationPaths paths)
    {
        var configured = Plugin.Instance?.ReadConfiguration(c => c.StreamDirectory);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(paths.CachePath, "livechannels")
            : configured;
    }

    private static string ScheduleFile(IApplicationPaths paths) => Path.Combine(ResolveStreamRoot(paths), ScheduleFileName);

    private List<ProgramEntry>? TryReadScheduleCache(Channel channel)
    {
        try
        {
            return ReadSchedules().TryGetValue(channel.Id, out var programs) && programs.Count > 0 ? programs : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the cached schedule for channel {Name}; rebuilding it", channel.Name);
            return null;
        }
    }

    private void WriteScheduleCache(Channel channel, IReadOnlyList<ProgramEntry> programs)
    {
        try
        {
            // The whole channel->schedule map lives in one file, so read-modify-write it under a lock: the guide
            // refresh updates one channel at a time and must not clobber the others' entries.
            lock (ScheduleLock)
            {
                var all = ReadSchedules();
                all[channel.Id] = programs as List<ProgramEntry> ?? programs.ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(_scheduleFile)!);

                // Write to a unique temp file then move it into place so a concurrent reader never sees a
                // half-written cache.
                var temp = _scheduleFile + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, all, ScheduleCacheJson);
                }

                File.Move(temp, _scheduleFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the schedule cache for channel {Name}", channel.Name);
        }
    }

    // The whole channel id -> schedule map read from schedule.json, or an empty map when there is no cache yet.
    private Dictionary<string, List<ProgramEntry>> ReadSchedules()
    {
        if (!File.Exists(_scheduleFile))
        {
            return new Dictionary<string, List<ProgramEntry>>(StringComparer.Ordinal);
        }

        using var stream = File.OpenRead(_scheduleFile);
        return JsonSerializer.Deserialize<Dictionary<string, List<ProgramEntry>>>(stream, ScheduleCacheJson)
            ?? new Dictionary<string, List<ProgramEntry>>(StringComparer.Ordinal);
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

        var ratings = new RatingFilter(
            ResolveRatingScore(channel.MinOfficialRating),
            ResolveRatingScore(channel.MaxOfficialRating),
            channel.IncludeUnrated);
        var kidsScore = ResolveRatingScore(channel.KidsRatingThreshold);
        var kinds = channel.IncludeHomeVideos ? PlayableKindsWithHomeVideos : PlayableKinds;

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

    // Per-bucket caps for the Popular channel. Each kind is drawn from three sources: recently added, top
    // community rating, and most watched across all users. A short or empty source just yields fewer items.
    private const int MovieRecent = 9;
    private const int MovieRated = 9;
    private const int MovieWatched = 6;
    private const int SeriesRecent = 3;
    private const int SeriesRated = 3;
    private const int SeriesWatched = 2;

    // How many of each user's most-played episodes to scan when ranking the most-watched series.
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
            EpisodesPerBlock = src.EpisodesPerBlock,
            KeepMultiPartTogether = src.KeepMultiPartTogether,
            IncludeSpecials = src.IncludeSpecials,
            Shuffle = src.Shuffle,
            ShuffleEpisodes = src.ShuffleEpisodes,
            FavorKind = src.FavorKind,
            FavorStrength = src.FavorStrength,
            SubtitleBurnIn = src.SubtitleBurnIn
        };
    }

    // Resolves the Popular channel's loop. Movies and shows are each drawn from three buckets (recent additions,
    // top community rating, most watched across all users) up to their per-bucket caps, de-duplicated so a title
    // that qualifies in two buckets is counted once and the next candidate backfills it. Series then expand to
    // their episodes.
    private IReadOnlyList<ProgramEntry> ResolvePopularPrograms(Channel channel)
    {
        var ratings = new RatingFilter(
            ResolveRatingScore(channel.MinOfficialRating),
            ResolveRatingScore(channel.MaxOfficialRating),
            channel.IncludeUnrated);
        var kidsScore = ResolveRatingScore(channel.KidsRatingThreshold);
        var users = _userManager.GetUsers().ToList();
        var lookup = new Dictionary<Guid, BaseItem>();

        // Keep only the candidates the channel's rating band allows, so the rating cap applies to the bucket
        // selection itself. The queries over-fetch (x4) so a tight cap still leaves enough to fill the quotas.
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

        var movieIds = SelectBuckets(
            (Ids(Recent(BaseItemKind.Movie, MovieRecent * 4)), MovieRecent),
            (Ids(TopRated(BaseItemKind.Movie, MovieRated * 4)), MovieRated),
            (Ids(MostWatchedMovies(users, MovieWatched * 4)), MovieWatched));

        var seriesIds = SelectBuckets(
            (Ids(Recent(BaseItemKind.Series, SeriesRecent * 4)), SeriesRecent),
            (Ids(TopRated(BaseItemKind.Series, SeriesRated * 4)), SeriesRated),
            (Ids(MostWatchedSeries(users, SeriesWatched * 4)), SeriesWatched));

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
            PopularChannelId,
            channel.FavorKind,
            channel.FavorStrength);

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
    private List<BaseItem> MostWatchedMovies(List<User> users, int count)
    {
        if (users.Count == 0)
        {
            return new List<BaseItem>();
        }

        var candidates = new Dictionary<Guid, BaseItem>();
        foreach (var user in users)
        {
            foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.PlayCount, SortOrder.Descending) },
                Limit = count * 5
            }))
            {
                candidates[movie.Id] = movie;
            }
        }

        return candidates.Values
            .OrderByDescending(movie => TotalPlayCount(movie, users))
            .Take(count)
            .ToList();
    }

    // Series watched most across the whole server: each user's most-played episodes are tallied to their series,
    // summed over every user, and the top series are returned.
    private List<BaseItem> MostWatchedSeries(List<User> users, int count)
    {
        if (users.Count == 0)
        {
            return new List<BaseItem>();
        }

        var plays = new Dictionary<Guid, long>();
        foreach (var user in users)
        {
            foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.PlayCount, SortOrder.Descending) },
                Limit = WatchedEpisodeScan
            }))
            {
                if (item is Episode ep && ep.SeriesId != Guid.Empty)
                {
                    var playCount = _userDataManager.GetUserData(user, item)?.PlayCount ?? 0;
                    if (playCount > 0)
                    {
                        plays.TryGetValue(ep.SeriesId, out var current);
                        plays[ep.SeriesId] = current + playCount;
                    }
                }
            }
        }

        var series = new List<BaseItem>();
        foreach (var id in plays.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(count))
        {
            if (_libraryManager.GetItemById(id) is { } found)
            {
                series.Add(found);
            }
        }

        return series;
    }

    // An item's total play count across every user.
    private long TotalPlayCount(BaseItem item, List<User> users)
    {
        long total = 0;
        foreach (var user in users)
        {
            total += _userDataManager.GetUserData(user, item)?.PlayCount ?? 0;
        }

        return total;
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
