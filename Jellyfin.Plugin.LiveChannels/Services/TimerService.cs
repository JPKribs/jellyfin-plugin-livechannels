using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Persists the DVR timers clients schedule against the virtual channels, so a timer created from the guide
/// survives restarts and appears in every client's scheduled list until it is cancelled or its window has fully
/// passed. The channels replay library content that already exists on disk, so no recording file is ever
/// produced when a timer's window arrives; the timer surface exists so client DVR flows (schedule, upcoming,
/// cancel) behave normally. The store is one JSON file under Jellyfin's data directory, not the cache: timers
/// are user data and must survive cache clears and schedule rebuilds.
/// </summary>
public sealed class TimerService
{
    private static readonly JsonSerializerOptions TimerJson = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly ILogger<TimerService> _logger;
    private readonly string _file;
    private readonly object _gate = new();

    private List<TimerInfo> _timers = new();
    private List<SeriesTimerInfo> _seriesTimers = new();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimerService"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths, used to place the timer store under Jellyfin's data directory.</param>
    /// <param name="logger">The logger.</param>
    public TimerService(IApplicationPaths appPaths, ILogger<TimerService> logger)
    {
        _logger = logger;
        _file = Path.Combine(appPaths.DataPath, "livechannels", "timers.json");
    }

    /// <summary>
    /// Returns the current single-program timers. Timers whose window (including post padding) has fully passed
    /// age out of the store here, and each remaining timer's status reflects whether its window is underway.
    /// </summary>
    /// <returns>The current timers.</returns>
    public IReadOnlyList<TimerInfo> GetTimers()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            if (_timers.RemoveAll(t => t.EndDate.AddSeconds(t.PostPaddingSeconds) <= now) > 0)
            {
                Persist();
            }

            // Status is derived from the clock on every read rather than persisted, so it can never go stale.
            foreach (var timer in _timers)
            {
                timer.Status = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds) <= now
                    ? RecordingStatus.InProgress
                    : RecordingStatus.New;
            }

            return _timers.ToList();
        }
    }

    /// <summary>
    /// Returns the current series timers.
    /// </summary>
    /// <returns>The current series timers.</returns>
    public IReadOnlyList<SeriesTimerInfo> GetSeriesTimers()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _seriesTimers.ToList();
        }
    }

    /// <summary>
    /// Adds or updates a timer, assigning it an id when it arrives without one (a create). Scheduling a program
    /// that already has a timer replaces the existing timer instead of stacking a duplicate.
    /// </summary>
    /// <param name="info">The timer.</param>
    /// <returns>The timer's id.</returns>
    public string SaveTimer(TimerInfo info)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(info.Id))
            {
                info.Id = NewTimerId();
            }

            _timers.RemoveAll(t => string.Equals(t.Id, info.Id, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(info.ProgramId) && string.Equals(t.ProgramId, info.ProgramId, StringComparison.Ordinal)));
            _timers.Add(info);
            Persist();
            return info.Id;
        }
    }

    /// <summary>
    /// Adds or updates a series timer, assigning it an id when it arrives without one (a create).
    /// </summary>
    /// <param name="info">The series timer.</param>
    /// <returns>The series timer's id.</returns>
    public string SaveSeriesTimer(SeriesTimerInfo info)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(info.Id))
            {
                info.Id = NewTimerId();
            }

            _seriesTimers.RemoveAll(t => string.Equals(t.Id, info.Id, StringComparison.Ordinal));
            _seriesTimers.Add(info);
            Persist();
            return info.Id;
        }
    }

    /// <summary>
    /// Removes a timer. Unknown ids are ignored: the timer may already have aged out of the store.
    /// </summary>
    /// <param name="timerId">The timer id.</param>
    public void CancelTimer(string timerId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_timers.RemoveAll(t => string.Equals(t.Id, timerId, StringComparison.Ordinal)) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// Removes a series timer along with every timer that belongs to it.
    /// </summary>
    /// <param name="timerId">The series timer id.</param>
    public void CancelSeriesTimer(string timerId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var removed = _seriesTimers.RemoveAll(t => string.Equals(t.Id, timerId, StringComparison.Ordinal));
            removed += _timers.RemoveAll(t => string.Equals(t.SeriesTimerId, timerId, StringComparison.Ordinal));
            if (removed > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// Builds the defaults a client's new-recording dialog is seeded with, carrying over the program's identity
    /// and airing window when the dialog was opened from a guide entry.
    /// </summary>
    /// <param name="program">The program the dialog was opened from, or null for a blank timer.</param>
    /// <returns>The seeded defaults.</returns>
    public static SeriesTimerInfo NewTimerDefaults(ProgramInfo? program)
    {
        var defaults = new SeriesTimerInfo
        {
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false,
            Days = Enum.GetValues<DayOfWeek>().ToList()
        };

        if (program is not null)
        {
            defaults.ChannelId = program.ChannelId;
            defaults.ProgramId = program.Id;
            defaults.SeriesId = program.SeriesId;
            defaults.Name = program.Name;
            defaults.Overview = program.Overview;
            defaults.StartDate = program.StartDate;
            defaults.EndDate = program.EndDate;
            defaults.Days = new List<DayOfWeek> { program.StartDate.DayOfWeek };
        }

        return defaults;
    }

    private static string NewTimerId() => "lc_timer_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            if (File.Exists(_file))
            {
                using var stream = File.OpenRead(_file);
                var loaded = JsonSerializer.Deserialize<TimerFile>(stream, TimerJson);
                _timers = loaded?.Timers ?? new List<TimerInfo>();
                _seriesTimers = loaded?.SeriesTimers ?? new List<SeriesTimerInfo>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not read the timer store at {File}; starting empty", _file);
        }

        _loaded = true;
    }

    // Writes the store atomically (temp file + move) so a concurrent reader never sees a half-written file. On
    // failure the in-memory timers still serve this run and the loss only surfaces after a restart, so log it
    // at the default level.
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, new TimerFile { Timers = _timers, SeriesTimers = _seriesTimers }, TimerJson);
            }

            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not persist timers to {File}; changes apply this run but will not survive a restart", _file);
        }
    }

    // The on-disk payload: both timer kinds in one file, so every save is one atomic unit.
    private sealed class TimerFile
    {
        public List<TimerInfo> Timers { get; set; } = new();

        public List<SeriesTimerInfo> SeriesTimers { get; set; } = new();
    }
}
