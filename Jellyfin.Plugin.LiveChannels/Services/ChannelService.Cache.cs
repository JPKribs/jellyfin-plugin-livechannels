using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LiveChannels.Models;
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

    /// <summary>The name of the directory, under the stream root, that holds the per-channel schedule files.</summary>
    public const string ScheduleDirName = "schedule";

    private readonly string _scheduleDir;

    // Observed item durations (seconds by item id), recorded when a full item plays through and its real
    // length drifts from the metadata runtime schedules are built on. Loaded once from a small JSON file
    // beside the schedule cache; schedule builds prefer these, so seams stop carrying the drift as a
    // timestamp gap (or, worse, an overlap when a file runs LONGER than its metadata claims).
    private readonly ConcurrentDictionary<Guid, ObservedDuration> _observedDurations = new();
    private readonly object _durationsLock = new();
    private bool _durationsLoaded;

    private const string DurationsFileName = "durations.json";

    private string DurationsFile => Path.Combine(_scheduleDir, DurationsFileName);

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

        lock (_durationsLock)
        {
            try
            {
                Directory.CreateDirectory(_scheduleDir);
                var temp = DurationsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_observedDurations.ToDictionary(kv => kv.Key, kv => kv.Value), ScheduleCacheJson));
                File.Move(temp, DurationsFile, overwrite: true);
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
            _observedDurations.TryRemove(itemId, out _);
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
                if (File.Exists(DurationsFile))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<Guid, ObservedDuration>>(File.ReadAllText(DurationsFile), ScheduleCacheJson);
                    foreach (var (id, observed) in loaded ?? new Dictionary<Guid, ObservedDuration>())
                    {
                        _observedDurations[id] = observed;
                    }
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
                    // Delete only the per-channel schedule files. The observed-durations file survives a
                    // channel edit: the durations describe the media files, not the channel configuration,
                    // and each one costs a full airing to re-learn.
                    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        if (!string.Equals(Path.GetFileName(file), DurationsFileName, StringComparison.Ordinal))
                        {
                            File.Delete(file);
                        }
                    }
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
}
