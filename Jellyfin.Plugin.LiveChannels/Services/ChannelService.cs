using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    // Decoded schedules held in memory while a channel is being watched, keyed by channel number. Populated on
    // tune-in (so repeat reads skip the disk read and JSON parse) and released the moment a channel's last session
    // closes, so an idle channel holds nothing. The on-disk per-channel file is the source of truth; this is just
    // a hot copy. The schedule already carries every item's probed media metadata (HDR, interlace, bit depth,
    // audio, subtitles), so a tune-in served from here makes no media-stream queries at all. Static (the service is
    // a singleton) so the static configuration-change handler can flush it alongside the on-disk cache, ensuring a
    // filter or channel edit is never served from a stale hot copy of a channel that happens to be on screen.
    private static readonly ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>> MemorySchedules = new();

    // Resolved schedules cached on disk so the expensive library resolve (and per-item media probe) runs once -- on
    // guide refresh -- and every tune-in reads the result instead of rebuilding it on the stream's start-up critical
    // path (a large channel's rebuild took ~40s, past the live-playlist deadline, and the client reported "Failed to
    // load"). One file per channel lives under the stream root in a `schedule` subdirectory, named by channel number
    // (e.g. <cache>/livechannels/schedule/0.json), so a tune-in reads and parses only the one channel it needs, and a
    // guide refresh rewrites only the channel that changed. Cleared when the plugin configuration changes so channel
    // edits take effect on the next guide refresh or tune-in.
    private static readonly JsonSerializerOptions ScheduleCacheJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly object ScheduleLock = new();

    /// <summary>The name of the directory, under the stream root, that holds the per-channel schedule files.</summary>
    public const string ScheduleDirName = "schedule";

    private readonly string _scheduleDir;

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
        _scheduleDir = ScheduleDir(appPaths);
        _logger = logger;
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

    // Whether the video is HDR (PQ or HLG), keyed off the colour transfer: smpte2084 = HDR10/PQ,
    // arib-std-b67 = HLG.
    internal static bool ComputeIsHdr(MediaStream? video)
    {
        var transfer = video?.ColorTransfer;
        return string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
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
    /// Picks the subtitle track to burn into an item for the given mode. <see cref="SubtitleBurnInMode.Forced"/>
    /// burns only the forced track, except when the default audio track is in a known non-English language, where
    /// it behaves like <see cref="SubtitleBurnInMode.Always"/> so foreign-language content stays followable.
    /// <see cref="SubtitleBurnInMode.Always"/> burns the forced track when present, otherwise the default or first.
    /// </summary>
    /// <param name="program">The program, carrying its subtitle streams and default-audio language probed at refresh.</param>
    /// <param name="mode">The channel's subtitle burn-in mode.</param>
    /// <returns>The chosen subtitle's index among the item's subtitle streams and whether it is text-based, or <c>null</c> when nothing should be burned in.</returns>
    public static (int RelativeIndex, bool IsText)? FindBurnInSubtitle(ProgramEntry program, SubtitleBurnInMode mode)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (mode == SubtitleBurnInMode.Never)
        {
            return null;
        }

        var subtitles = program.Subtitles;

        // Forced always wins when present, in either mode.
        for (var i = 0; i < subtitles.Count; i++)
        {
            if (subtitles[i].IsForced)
            {
                return (subtitles[i].RelativeIndex, subtitles[i].IsText);
            }
        }

        // "Forced only" also burns subtitles when the audio we play is not in the configured default language, so
        // foreign-language content stays followable. Audio in the default language, or untagged audio, shows
        // nothing without a forced track, so ordinary content is never subtitled by surprise.
        var defaultLanguage = Plugin.Instance?.ReadConfiguration(c => c.DefaultSubtitleLanguage) ?? string.Empty;
        var audioLanguage = program.DefaultAudioLanguage;
        var forcedForForeignAudio = mode == SubtitleBurnInMode.Forced
            && !string.IsNullOrEmpty(audioLanguage)
            && !string.IsNullOrEmpty(defaultLanguage)
            && !string.Equals(audioLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase);

        if ((mode == SubtitleBurnInMode.Always || forcedForForeignAudio) && subtitles.Count > 0)
        {
            // Both "Always" and non-English-audio "Forced only" burn the same track "Always" picks: the default
            // subtitle, or the first one when none is flagged default.
            var chosen = 0;
            for (var i = 0; i < subtitles.Count; i++)
            {
                if (subtitles[i].IsDefault)
                {
                    chosen = i;
                    break;
                }
            }

            return (subtitles[chosen].RelativeIndex, subtitles[chosen].IsText);
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
    /// never pays the full library resolve or per-item media probe. Builds (and caches) it only when no cache
    /// exists yet. The decoded loop is held in memory until the channel's last session closes (see
    /// <see cref="ReleaseFromMemory"/>), so repeat tune-ins skip even the disk read and JSON parse.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> ResolvePrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (MemorySchedules.TryGetValue(channel.Number, out var hot))
        {
            return hot;
        }

        var stopwatch = Stopwatch.StartNew();
        var cached = TryReadScheduleCache(channel);
        var programs = cached ?? RefreshPrograms(channel);
        stopwatch.Stop();

        // This sits directly on the tune-in critical path (observed: a silent 13-second full rebuild of an
        // 8419-item channel on an N100 made the first tune-in time out), so any non-trivial load is visible at
        // the default log level, with its SOURCE: "rebuilt" means the disk cache was missing or unreadable and
        // this tune-in paid a full library resolve that belongs at guide-refresh time.
        if (stopwatch.ElapsedMilliseconds > 500)
        {
            _logger.LogInformation(
                "Live Channels: schedule for {Name} ({Count} items) {Source} in {Seconds:F1}s on the tune-in path",
                channel.Name,
                programs.Count,
                cached is null ? "REBUILT (disk cache missing or unreadable)" : "loaded from disk",
                stopwatch.Elapsed.TotalSeconds);
        }

        MemorySchedules[channel.Number] = programs;
        return programs;
    }

    /// <summary>
    /// Builds a channel's program loop and overwrites its on-disk cache. Called on guide refresh so the schedule
    /// is built once (probing each item's media metadata off the tune-in path) and every tune-in until the next
    /// refresh reuses it. Drops any in-memory copy so the next tune-in reloads the freshly written schedule.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The freshly built, ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> RefreshPrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var programs = BuildPrograms(channel);
        WriteScheduleCache(channel, programs);
        // Keep the freshly built schedule HOT: this build already paid the full library resolve, and dropping it
        // meant the next tune-in re-read (or, if the cache read failed, silently re-BUILT) it on the critical
        // path — observed as a 13-second first-tune stall on a large channel. Guide refreshes run regularly, so
        // in practice every enabled channel stays warm; ReleaseFromMemory still trims a channel after its last
        // session closes, keeping long-idle memory bounded between refreshes.
        MemorySchedules[channel.Number] = programs;
        return programs;
    }

    /// <summary>
    /// Releases a channel's in-memory schedule once it has no more active sessions, so an unwatched channel holds
    /// nothing in memory. The on-disk cache remains, so the next tune-in reloads it. A no-op when nothing is cached.
    /// </summary>
    /// <param name="channelNumber">The channel number whose hot schedule should be dropped.</param>
    public void ReleaseFromMemory(int channelNumber) => MemorySchedules.TryRemove(channelNumber, out _);

    /// <summary>
    /// Deletes the cached schedule. Called when the plugin configuration changes so a channel edit is never served
    /// from a stale cache; the next guide refresh (or tune-in) rebuilds it.
    /// </summary>
    /// <param name="paths">The application paths, used to locate the stream root the cache lives in.</param>
    public static void ClearScheduleCache(IApplicationPaths paths)
    {
        // Flush the in-memory hot copies too, so a channel currently on screen does not keep serving its old
        // schedule (with the old filters) to new tune-ins until the asynchronous guide refresh reaches it.
        MemorySchedules.Clear();

        try
        {
            lock (ScheduleLock)
            {
                var dir = ScheduleDir(paths);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
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
    /// both agree on where the schedule cache lives.
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

    /// <summary>The directory, under the stream root, that holds the per-channel schedule files.</summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The schedule cache directory.</returns>
    public static string ScheduleDir(IApplicationPaths paths) => Path.Combine(ResolveStreamRoot(paths), ScheduleDirName);

    // The on-disk path of one channel's schedule file, named by channel number (e.g. .../schedule/0.json).
    private string ScheduleFileFor(Channel channel)
        => Path.Combine(_scheduleDir, channel.Number.ToString(CultureInfo.InvariantCulture) + ".json");

    private List<ProgramEntry>? TryReadScheduleCache(Channel channel)
    {
        try
        {
            var file = ScheduleFileFor(channel);
            if (!File.Exists(file))
            {
                return null;
            }

            using var stream = File.OpenRead(file);
            var programs = JsonSerializer.Deserialize<List<ProgramEntry>>(stream, ScheduleCacheJson);
            return programs is { Count: > 0 } ? programs : null;
        }
        catch (Exception ex)
        {
            // Warning, not debug: a failed cache read silently costs the next tune-in a full library rebuild on
            // its critical path, so the cause must be visible at the default log level.
            _logger.LogWarning(ex, "Live Channels: could not read the cached schedule for channel {Name}; the next tune-in will rebuild it", channel.Name);
            return null;
        }
    }

    private void WriteScheduleCache(Channel channel, IReadOnlyList<ProgramEntry> programs)
    {
        try
        {
            // Each channel owns its own file, so a write touches only this channel. The lock still serialises writes
            // so two refreshes cannot interleave their temp-file moves, and the atomic move keeps a concurrent
            // reader from ever seeing a half-written file.
            lock (ScheduleLock)
            {
                var file = ScheduleFileFor(channel);
                Directory.CreateDirectory(_scheduleDir);

                var temp = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, programs, ScheduleCacheJson);
                }

                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the schedule cache for channel {Name}", channel.Name);
        }
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
        var years = new YearFilter(channel.Years);
        var kinds = channel.IncludeHomeVideos ? PlayableKindsWithHomeVideos : PlayableKinds;

        var byId = new Dictionary<Guid, BaseItem>();
        var libraryIds = new List<Guid>();
        foreach (var source in channel.Sources)
        {
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
        // library; the rating floors and year set are simple per-item checks.
        var studios = BuildStudioSet(channel.Studios);
        var seriesStudios = studios.Count > 0 ? BuildSeriesStudioMap(libraryIds) : new Dictionary<Guid, HashSet<string>>();
        var peopleAllowed = channel.People.Any(p => p.Id != Guid.Empty)
            ? ResolvePeopleAllowed(channel.People, libraryIds, kinds)
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
            channel.Shuffle,
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
            channel.Shuffle,
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

    private static ProgramEntry? ToEntry(BaseItem? item, int? kidsScore, IReadOnlyList<MediaStream> streams)
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

        // Probe the media metadata the live stream needs to choose its decode pipeline and burn-in track, once,
        // here at refresh, from the streams already read. The stream pipeline reads these off the cached entry and
        // never queries the media streams itself.
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var defaultAudio = DefaultAudio(streams);

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
