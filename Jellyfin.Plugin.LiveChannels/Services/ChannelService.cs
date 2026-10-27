using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Resolves configured channels into the ordered item loops their schedule and live streams are built from.
/// </summary>
/// <remarks>
/// The implementation is split across partial files by concern: <c>ChannelService.Resolution.cs</c> (library
/// sources to a probed program loop), <c>ChannelService.Popular.cs</c> (the built-in Popular channel),
/// <c>ChannelService.Filters.cs</c> (the per-item content filters), <c>ChannelService.Cache.cs</c> (the on-disk
/// schedule and observed-durations persistence), and <c>ChannelService.Subtitles.cs</c> (burn-in subtitle
/// selection and extraction). This file holds the constructor and the public facade.
/// </remarks>
public partial class ChannelService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IPathManager _pathManager;
    private readonly ILocalizationManager _localization;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ChannelService> _logger;

    // Decoded schedules held in memory while a channel is being watched, keyed by channel number. Populated on
    // tune-in (and by a DVR pass resolving a program) so repeat reads skip the disk read and JSON parse, and
    // released when the channel's last session closes. A guide refresh updates a hot copy IN PLACE but never
    // pins a cold one, so a channel nobody tunes holds nothing here. The on-disk per-channel file is the source
    // of truth; this is just a hot copy. The schedule already carries every item's probed media metadata (HDR,
    // interlace, bit depth, audio, subtitles), so a tune-in served from here makes no media-stream queries at
    // all. Static (the service is a singleton) so the static configuration-change handler can flush it alongside
    // the on-disk cache, ensuring a filter or channel edit is never served from a stale hot copy of a channel
    // that happens to be on screen.
    private static readonly ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>> MemorySchedules = new();

    // Hot copies a configuration save took out of service, kept only until each channel's rebuild splices onto it.
    private static readonly ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>> SupersededSchedules = new();

    // Per channel, how many loops a refresh has written, so a disk read that started before a refresh cannot pin
    // the loop it read over the one the refresh just wrote.
    private static readonly ConcurrentDictionary<int, long> ScheduleVersions = new();

    private static long ScheduleVersion(int channelNumber) => ScheduleVersions.TryGetValue(channelNumber, out var v) ? v : 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager used to resolve items.</param>
    /// <param name="mediaSourceManager">The media source manager used to inspect subtitle streams.</param>
    /// <param name="subtitleEncoder">The subtitle encoder used to extract (and cache) embedded subtitles for burn-in.</param>
    /// <param name="pathManager">The path manager, used to locate the fonts Jellyfin extracted from a media file's attachments so burned-in ASS subtitles render in the font they were authored with.</param>
    /// <param name="localization">The localization manager used to rank official ratings.</param>
    /// <param name="userManager">The user manager, used to read server-wide watch data for the Popular channel.</param>
    /// <param name="userDataManager">The user-data manager, used to read per-item play counts for the Popular channel.</param>
    /// <param name="appPaths">The application paths, used to locate the stream root the schedule cache lives in.</param>
    /// <param name="logger">The logger.</param>
    public ChannelService(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISubtitleEncoder subtitleEncoder, IPathManager pathManager, ILocalizationManager localization, IUserManager userManager, IUserDataManager userDataManager, IApplicationPaths appPaths, ILogger<ChannelService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _pathManager = pathManager;
        _localization = localization;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _scheduleDir = ScheduleDir(appPaths);
        Volatile.Write(ref _activeScheduleDir, _scheduleDir);
        _durationsStore = new JsonFileStore<Dictionary<Guid, ObservedDuration>>(Path.Combine(_scheduleDir, DurationsFileName), logger);
        _logger = logger;
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
    /// <param name="keepInMemory">Whether a loop read from disk stays in memory. Background readers (the recording
    /// heartbeat) pass <c>false</c>, so a channel nobody watches holds nothing in memory just because a series rule
    /// looks at it every minute.</param>
    /// <returns>The ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> ResolvePrograms(Channel channel, bool keepInMemory = true)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (MemorySchedules.TryGetValue(channel.Number, out var hot))
        {
            return hot;
        }

        // Captured before the read: a guide refresh that writes a newer loop while this read is under way bumps the
        // channel's version, and this (older) loop is then returned but not pinned over the refreshed one.
        var generation = CacheGeneration;
        var version = ScheduleVersion(channel.Number);
        var stopwatch = Stopwatch.StartNew();
        var cached = TryReadScheduleCache(channel);
        var programs = (IReadOnlyList<ProgramEntry>?)cached ?? RefreshPrograms(channel);
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

        // A configuration save that landed during the load has already cleared the cache; pinning this pre-save
        // schedule would serve it to every later tune-in until the next guide refresh.
        if (keepInMemory && CacheGeneration == generation && (cached is null || ScheduleVersion(channel.Number) == version))
        {
            MemorySchedules[channel.Number] = programs;
        }

        return programs;
    }

    /// <summary>
    /// Builds a channel's program loop and overwrites its on-disk cache. Called on guide refresh so the schedule
    /// is built once (probing each item's media metadata off the tune-in path) and every tune-in until the next
    /// refresh reuses it. Drops any in-memory copy so the next tune-in reloads the freshly written schedule.
    /// The new loop is spliced onto the previous one (see <see cref="Splice"/>), so the rebuild keeps whatever is
    /// airing now instead of rewriting the timeline under it.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The freshly built, ordered program loop.</returns>
    public IReadOnlyList<ProgramEntry> RefreshPrograms(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var generation = CacheGeneration;

        // Build from the configuration as it stands now, after the generation was captured: a caller's copy of the
        // channel may predate a save, and a loop built from it would otherwise be cached under the new generation.
        // A save landing after this point changes the generation, and the result is then not cached.
        if (Plugin.Instance is not null && FindChannel(channel.Id) is { } current)
        {
            channel = current;
        }

        // The loop to splice onto: the one in service, else the one a configuration save set aside, so a save
        // (a channel edit, a language change) does not move what is airing either.
        IReadOnlyList<ProgramEntry>? previous = MemorySchedules.TryGetValue(channel.Number, out var hotPrevious)
            ? hotPrevious
            : (IReadOnlyList<ProgramEntry>?)TryReadScheduleCache(channel) ?? SupersededLoop(channel);
        var programs = Splice(channel, previous, BuildPrograms(channel), DateTime.UtcNow);

        // A build takes seconds (tens of seconds on a large channel); a configuration save during it means this loop
        // was built from the old channel settings. The caller still gets it, but it is neither cached nor pinned.
        if (!WriteScheduleCache(channel, programs, generation))
        {
            return programs;
        }

        ForgetSuperseded(channel);
        ScheduleVersions.AddOrUpdate(channel.Number, 1, (_, v) => v + 1);

        // Update the hot copy only for a channel that is already hot (someone is watching it, or it was tuned
        // recently), so its live viewers see the fresh schedule without a disk round-trip. A channel that is
        // NOT hot is deliberately left cold: pinning every enabled channel here kept several MB of decoded
        // schedule per large channel in memory forever, refreshed daily, for channels nobody ever tuned. A cold
        // channel's next tune-in reads the just-written disk cache (a fast parse, logged when slow), not a rebuild.
        if (MemorySchedules.TryGetValue(channel.Number, out var hot))
        {
            MemorySchedules.TryUpdate(channel.Number, programs, hot);
        }

        return programs;
    }

    /// <summary>
    /// Splices a rebuilt loop onto the previous one. Every build changes the loop (the daily series rotation, newly
    /// added or removed items, learned durations), and every schedule is measured from a fixed anchor, so an
    /// unspliced rebuild rewrites the whole timeline -- including the program on now -- the moment it lands: the
    /// stream jumps mid-show, and the guide ends up holding the old timeline and the new one side by side
    /// (Jellyfin keeps the previous refresh's programs whenever any channel errors). The splice finds the item the
    /// previous loop has on air at <paramref name="nowUtc"/>, rotates the new loop to begin with it (the loop is a
    /// cycle, so its order is untouched), and anchors it at that item's original start, so the airing program
    /// keeps its slot and the new loop carries on from its end.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="previous">The loop in use before this rebuild, or <c>null</c> when there is none (a first build,
    /// or one after a configuration save cleared the cache).</param>
    /// <param name="built">The freshly built loop.</param>
    /// <param name="nowUtc">The rebuild instant.</param>
    /// <returns>The loop to cache and serve.</returns>
    internal ProgramLoop Splice(Channel channel, IReadOnlyList<ProgramEntry>? previous, IReadOnlyList<ProgramEntry> built, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(built);
        if (previous is null || previous.Count == 0 || built.Count == 0)
        {
            return new ProgramLoop(built, null);
        }

        ScheduledProgram? airing = null;
        foreach (var slot in BuildTimeline(channel, previous, nowUtc, nowUtc.AddTicks(1)))
        {
            if (slot.Start <= nowUtc && nowUtc < slot.Stop)
            {
                airing = slot;
                break;
            }
        }

        if (airing is null)
        {
            return new ProgramLoop(built, null);
        }

        // An item the rebuild dropped (deleted, or filtered out) cannot continue; the new loop then simply starts
        // at the old program's slot, which still keeps every later program on the new loop's own boundaries.
        var index = 0;
        for (var i = 0; i < built.Count; i++)
        {
            if (built[i].ItemId == airing.Program.ItemId)
            {
                index = i;
                break;
            }
        }

        var rotated = index == 0 ? built : built.Skip(index).Concat(built.Take(index)).ToList();
        return new ProgramLoop(rotated, airing.Start);
    }

    /// <summary>
    /// Releases a channel's in-memory schedule once it has no more active sessions, so an unwatched channel holds
    /// nothing in memory. The on-disk cache remains, so the next tune-in reloads it. A no-op when nothing is cached.
    /// </summary>
    /// <param name="channelNumber">The channel number whose hot schedule should be dropped.</param>
    public void ReleaseFromMemory(int channelNumber) => MemorySchedules.TryRemove(channelNumber, out _);
}
