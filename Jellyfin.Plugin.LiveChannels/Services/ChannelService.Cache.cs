using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.LiveChannels.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: on-disk persistence -- the per-channel schedule cache and the observed-durations file.
public partial class ChannelService
{
    // Resolved schedules cached on disk so the expensive library resolve (and per-item media probe) runs once -- on
    // guide refresh -- and every tune-in reads the result instead of rebuilding it on the stream's start-up critical
    // path (a large channel's rebuild took ~40s, past the live-playlist deadline, and the client reported "Failed to
    // load"). One file per channel lives under the stream root in a `schedule` subdirectory, named by channel number
    // (e.g. <cache>/livechannels/schedule/0.json), so a tune-in reads and parses only the one channel it needs, and a
    // guide refresh rewrites only the channel that changed. Cleared when the plugin configuration changes so channel
    // edits take effect on the next guide refresh or tune-in.
    private static readonly JsonSerializerOptions ScheduleCacheJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly object ScheduleLock = new();

    // The schedule directory the live service instance reads and writes. Fixed at construction (like the live-TV
    // service's stream root, a StreamDirectory change applies on restart), so a configuration save must clear THIS
    // directory too, not only the one the new configuration names, or the instance keeps serving a stale cache.
    private static string? _activeScheduleDir;

    // Bumped by every cache clear. A build or disk read captures it first and drops its result (no disk write, no
    // hot pin) when a clear landed meanwhile: that result was resolved from the pre-save channel configuration and
    // would otherwise outlive the clear until the next guide refresh.
    private static long _cacheGeneration;

    /// <summary>The name of the directory, under the stream root, that holds the per-channel schedule files.</summary>
    public const string ScheduleDirName = "schedule";

    /// <summary>
    /// The marker file the plugin drops in its stream root. It is how the cleanup sweeps and the configuration
    /// validator tell a directory the plugin owns from one that existed before: a configured stream directory
    /// that already has other content and no marker is refused rather than swept.
    /// </summary>
    public const string StreamRootMarkerName = ".livechannels";

    private readonly string _scheduleDir;

    // Observed item durations (seconds by item id), recorded when a full item plays through and its real
    // length drifts from the metadata runtime schedules are built on. Loaded once from a small JSON file
    // beside the schedule cache; schedule builds prefer these, so seams stop carrying the drift as a
    // timestamp gap (or, worse, an overlap when a file runs LONGER than its metadata claims).
    private readonly ConcurrentDictionary<Guid, ObservedDuration> _observedDurations = new();
    private readonly object _durationsLock = new();
    private bool _durationsLoaded;

    private const string DurationsFileName = "durations.json";

    private const string SupersededExtension = ".superseded";

    private readonly JsonFileStore<Dictionary<Guid, ObservedDuration>> _durationsStore;

    // One observation: the real playable length, plus the metadata runtime it was recorded against
    // (the staleness key -- see ObservedDurationTicks).
    private sealed record ObservedDuration(double Seconds, double MetadataSeconds);

    /// <summary>
    /// Records an item's observed playable length so the next schedule build uses the real duration.
    /// </summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="seconds">The observed length in seconds.</param>
    /// <param name="title">The item title, for the log line.</param>
    /// <param name="scheduledSeconds">The length the schedule had used, for the log line.</param>
    public void RecordObservedDuration(Guid itemId, double seconds, string title, double scheduledSeconds)
    {
        // The staleness key is the item's CURRENT metadata runtime, read from the library here -- NOT the
        // scheduled length, which may itself already be an observed override (storing that would fail the
        // metadata-match check at the next refresh and discard a perfectly good observation).
        var metadataTicks = _libraryManager.GetItemById(itemId)?.RunTimeTicks;
        if (metadataTicks is not > 0)
        {
            return; // The item vanished from the library, or has no runtime to validate against later.
        }

        var metadataSeconds = TimeSpan.FromTicks(metadataTicks.Value).TotalSeconds;
        EnsureDurationsLoaded();
        if (_observedDurations.TryGetValue(itemId, out var existing)
            && Math.Abs(existing.Seconds - seconds) < 1
            && Math.Abs(existing.MetadataSeconds - metadataSeconds) < 1)
        {
            return;
        }

        _observedDurations[itemId] = new ObservedDuration(seconds, metadataSeconds);
        _logger.LogInformation(
            "Live Channels: \"{Title}\" actually plays for {Observed:F1}s (schedule had used {Scheduled:F1}s, metadata says {Metadata:F1}s); schedules use the observed length from the next guide refresh",
            title,
            seconds,
            scheduledSeconds,
            metadataSeconds);

        SaveObservedDurations();
    }

    private void SaveObservedDurations()
    {
        lock (_durationsLock)
        {
            try
            {
                _durationsStore.Save(_observedDurations.ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist observed durations; the in-memory values still apply this run");
            }
        }
    }

    // The observed override for an item, or null. An observation only applies while the item's CURRENT
    // metadata runtime still matches the one it was recorded against: a mismatch means the file was
    // replaced or re-probed since, so the stale observation is discarded rather than overriding fresh truth.
    private long? ObservedDurationTicks(Guid itemId, long metadataTicks)
    {
        EnsureDurationsLoaded();
        if (!_observedDurations.TryGetValue(itemId, out var observed) || observed.Seconds <= 10)
        {
            return null;
        }

        var metadataSeconds = TimeSpan.FromTicks(metadataTicks).TotalSeconds;
        if (Math.Abs(observed.MetadataSeconds - metadataSeconds) > 1)
        {
            if (_observedDurations.TryRemove(itemId, out _))
            {
                SaveObservedDurations();
            }

            return null;
        }

        return (long)(observed.Seconds * TimeSpan.TicksPerSecond);
    }

    private void EnsureDurationsLoaded()
    {
        if (_durationsLoaded)
        {
            return;
        }

        lock (_durationsLock)
        {
            if (_durationsLoaded)
            {
                return;
            }

            try
            {
                // A missing or corrupt file loads as empty (the store logs the corrupt case).
                foreach (var (id, observed) in _durationsStore.Load())
                {
                    _observedDurations[id] = observed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the observed-durations file; starting empty");
            }

            _durationsLoaded = true;
        }
    }

    /// <summary>
    /// Deletes the cached schedule. Called when the plugin configuration changes so a channel edit is never served
    /// from a stale cache; the next guide refresh (or tune-in) rebuilds it.
    /// </summary>
    /// <param name="paths">The application paths, used to locate the stream root the cache lives in.</param>
    public static void ClearScheduleCache(IApplicationPaths paths)
    {
        // Invalidate in-flight builds and reads before anything is deleted, so none of them can write or pin the
        // pre-save schedule after this clear.
        Interlocked.Increment(ref _cacheGeneration);

        // Flush the in-memory hot copies too, so a channel currently on screen does not keep serving its old
        // schedule (with the old filters) to new tune-ins until the asynchronous guide refresh reaches it. They are
        // kept aside, never served, only so the rebuild can splice onto them (see SupersededLoop).
        // Superseded copies from an earlier save whose channel was never rebuilt (deleted or disabled since) are
        // dropped here rather than kept forever; one still owed a rebuild splices from its file on disk instead.
        SupersededSchedules.Clear();
        foreach (var number in MemorySchedules.Keys)
        {
            if (MemorySchedules.TryRemove(number, out var hot))
            {
                SupersededSchedules[number] = hot;
            }
        }

        var configured = ScheduleDir(paths);
        var active = Volatile.Read(ref _activeScheduleDir);
        lock (ScheduleLock)
        {
            ClearScheduleDir(configured);
            if (active is not null && !string.Equals(active, configured, StringComparison.Ordinal))
            {
                ClearScheduleDir(active);
            }
        }
    }

    // Deletes the per-channel schedule files (and any temp file a failed write left behind) in one directory. The
    // observed-durations file survives a channel edit: the durations describe the media files, not the channel
    // configuration, and each one costs a full airing to re-learn.
    private static void ClearScheduleDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                // The durations file and its store's own temp files are left alone (the durations store is not
                // serialised by this lock, so one of its temp files may be mid-write).
                var name = Path.GetFileName(file);
                if (name.StartsWith(DurationsFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                // A channel's schedule is set aside rather than deleted: it is no longer served, but the rebuild
                // splices onto it so what is airing keeps its slot (see SupersededLoop).
                if (name.EndsWith(".json", StringComparison.Ordinal))
                {
                    File.Move(file, Path.ChangeExtension(file, SupersededExtension), overwrite: true);
                }
                else if (name.EndsWith(".tmp", StringComparison.Ordinal))
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

    // The current cache generation, captured before a build or read so its result can be dropped if a clear lands.
    internal static long CacheGeneration => Interlocked.Read(ref _cacheGeneration);

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

    // Where a cleared schedule waits for its rebuild (e.g. .../schedule/0.superseded).
    private string SupersededFileFor(Channel channel) => Path.ChangeExtension(ScheduleFileFor(channel), SupersededExtension);

    /// <summary>
    /// The schedule a configuration save set aside for a channel, if its rebuild has not replaced it yet. Never
    /// served (it may hold the old filters or audio choices); a rebuild only splices onto it, so a save does not
    /// reshuffle what is airing.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The superseded loop, or <c>null</c>.</returns>
    internal IReadOnlyList<ProgramEntry>? SupersededLoop(Channel channel)
    {
        if (SupersededSchedules.TryGetValue(channel.Number, out var hot))
        {
            return hot;
        }

        var file = SupersededFileFor(channel);
        return File.Exists(file) ? ReadScheduleFile(file, channel) : null;
    }

    // Drops a channel's superseded schedule once its rebuild is written.
    private void ForgetSuperseded(Channel channel)
    {
        SupersededSchedules.TryRemove(channel.Number, out _);
        try
        {
            File.Delete(SupersededFileFor(channel));
        }
        catch (Exception)
        {
            // Best effort: a leftover is replaced by the next clear and never served.
        }
    }

    private ProgramLoop? TryReadScheduleCache(Channel channel)
    {
        var file = ScheduleFileFor(channel);
        return File.Exists(file) ? ReadScheduleFile(file, channel) : null;
    }

    private ProgramLoop? ReadScheduleFile(string file, Channel channel)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(file);

            // An EMPTY cached schedule is a valid answer, not a miss: a channel whose filters legitimately
            // match nothing would otherwise re-run the full library resolve on every tune-in, exactly the
            // critical-path cost this cache exists to prevent. Only a null deserialization (a corrupt file)
            // falls through to a rebuild. A bare array is the pre-splice format: the loop with no anchor.
            if (FirstToken(bytes) == '[')
            {
                var legacy = JsonSerializer.Deserialize<List<ProgramEntry>>(bytes, ScheduleCacheJson);
                return legacy is null ? null : new ProgramLoop(legacy, null);
            }

            var cached = JsonSerializer.Deserialize<CachedLoop>(bytes, ScheduleCacheJson);
            return cached?.Programs is null ? null : new ProgramLoop(cached.Programs, cached.AnchorUtc);
        }
        catch (Exception ex)
        {
            // Warning, not debug: a failed cache read silently costs the next tune-in a full library rebuild on
            // its critical path, so the cause must be visible at the default log level.
            _logger.LogWarning(ex, "Live Channels: could not read the cached schedule for channel {Name}; the next tune-in will rebuild it", channel.Name);
            return null;
        }
    }

    // The first non-whitespace byte of a JSON document, telling the legacy bare-array cache from the envelope.
    private static char FirstToken(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF))
            {
                return (char)b;
            }
        }

        return '\0';
    }

    // The on-disk schedule: the loop and its splice anchor (null when it was not spliced onto a previous build).
    private sealed record CachedLoop(DateTime? AnchorUtc, List<ProgramEntry>? Programs);

    // Writes the schedule unless a cache clear landed since `generation` was captured (the schedule then reflects
    // the pre-save configuration). Returns false when the write was skipped for that reason.
    internal bool WriteScheduleCache(Channel channel, ProgramLoop programs, long generation)
    {
        string? temp = null;
        try
        {
            // Each channel owns its own file, so a write touches only this channel. The lock still serialises writes
            // so two refreshes cannot interleave their temp-file moves, and the atomic move keeps a concurrent
            // reader from ever seeing a half-written file. The clear holds the same lock, so it cannot slip between
            // the generation check and the move.
            lock (ScheduleLock)
            {
                if (CacheGeneration != generation)
                {
                    return false;
                }

                var file = ScheduleFileFor(channel);
                Directory.CreateDirectory(_scheduleDir);

                temp = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, new CachedLoop(programs.AnchorUtc, programs.ToList()), ScheduleCacheJson);
                }

                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the schedule cache for channel {Name}", channel.Name);
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Best effort; the next configuration save sweeps stray temp files.
                }
            }
        }

        return true;
    }
}
