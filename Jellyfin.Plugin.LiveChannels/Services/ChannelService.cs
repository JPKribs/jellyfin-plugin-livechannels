using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Common.Configuration;
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

    // Decoded schedules held in memory while a channel is being watched, keyed by channel number. Populated on
    // tune-in (so repeat reads skip the disk read and JSON parse) and released the moment a channel's last session
    // closes, so an idle channel holds nothing. The on-disk per-channel file is the source of truth; this is just
    // a hot copy. The schedule already carries every item's probed media metadata (HDR, interlace, bit depth,
    // audio, subtitles), so a tune-in served from here makes no media-stream queries at all. Static (the service is
    // a singleton) so the static configuration-change handler can flush it alongside the on-disk cache, ensuring a
    // filter or channel edit is never served from a stale hot copy of a channel that happens to be on screen.
    private static readonly ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>> MemorySchedules = new();

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
}
