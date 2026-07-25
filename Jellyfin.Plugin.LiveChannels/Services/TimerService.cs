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
/// survives restarts and appears in every client's scheduled list until it is cancelled or fulfilled. When a
/// timer's window ends, <see cref="RecordingService"/> materializes the recording (the channels replay library
/// content that already exists on disk, so fulfilment links or copies the backing file into the Live TV
/// recordings folder rather than capturing the stream) and marks the timer completed here; completed timers
/// then age out of the store while the recording lives on as an ordinary library item. The store is one JSON
/// file under Jellyfin's data directory, not the cache: timers are user data and must survive cache clears
/// and schedule rebuilds.
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
    /// Returns the current single-program timers. Fulfilled (or failed) timers whose window has fully passed
    /// age out of the store here; expired timers the recording service still owes a materialization pass are
    /// kept, or dropping them on a read would race the pass that produces their recording. Every other timer's
    /// status reflects whether its window is underway.
    /// </summary>
    /// <returns>The current timers.</returns>
    public IReadOnlyList<TimerInfo> GetTimers()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            if (_timers.RemoveAll(t => IsTerminal(t.Status) && t.EndDate.AddSeconds(t.PostPaddingSeconds) <= now) > 0)
            {
                Persist();
            }

            // Non-terminal status is derived from the clock on every read rather than persisted, so it can
            // never go stale; terminal outcomes (completed, error) were set by the recording service and stick.
            foreach (var timer in _timers)
            {
                if (!IsTerminal(timer.Status))
                {
                    timer.Status = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds) <= now
                        ? RecordingStatus.InProgress
                        : RecordingStatus.New;
                }
            }

            return _timers.ToList();
        }
    }

    /// <summary>
    /// Returns the timers whose window has ended but whose recording has not yet been produced, for the
    /// recording service's materialization pass. The live instances are returned so a bumped
    /// <see cref="TimerInfo.RetryCount"/> can be persisted back through <see cref="SaveTimer"/>.
    /// </summary>
    /// <returns>The timers owed a recording.</returns>
    public IReadOnlyList<TimerInfo> TimersDueForRecording()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            return _timers.Where(t => !IsTerminal(t.Status) && t.EndDate <= now).ToList();
        }
    }

    /// <summary>
    /// Returns whether any timer already targets the program, so series expansion never stomps a timer the
    /// user created or edited themselves.
    /// </summary>
    /// <param name="programId">The program id.</param>
    /// <returns>Whether a timer exists for the program.</returns>
    public bool HasTimerForProgram(string programId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _timers.Any(t => string.Equals(t.ProgramId, programId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Records a timer's final outcome: completed with the recording it produced, or errored. Terminal timers
    /// age out on the next <see cref="GetTimers"/> read once their window has passed.
    /// </summary>
    /// <param name="timerId">The timer id.</param>
    /// <param name="status">The terminal status.</param>
    /// <param name="recordingPath">The materialized recording file, or <c>null</c> when none was produced.</param>
    public void CompleteTimer(string timerId, RecordingStatus status, string? recordingPath)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var timer = _timers.FirstOrDefault(t => string.Equals(t.Id, timerId, StringComparison.Ordinal));
            if (timer is null)
            {
                return;
            }

            timer.Status = status;
            timer.RecordingPath = recordingPath;
            Persist();
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
    /// Adds or updates a series timer, assigning it an id when it arrives without one (a create). Pressing
    /// record again on a program that already has a rule replaces the rule instead of stacking a duplicate.
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

            // Replacing a rule must carry its children along: a child left pointing at the removed rule's id
            // is an orphan no client can ever cancel (Jellyfin 404s resolving the dead rule id).
            var replaced = _seriesTimers.Where(t => string.Equals(t.Id, info.Id, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(info.ProgramId) && string.Equals(t.ProgramId, info.ProgramId, StringComparison.Ordinal))).Select(t => t.Id).ToList();
            _seriesTimers.RemoveAll(t => replaced.Contains(t.Id));
            foreach (var timer in _timers.Where(t => t.SeriesTimerId is not null && replaced.Contains(t.SeriesTimerId)))
            {
                timer.SeriesTimerId = info.Id;
            }

            _seriesTimers.Add(info);
            Persist();
            return info.Id;
        }
    }

    /// <summary>
    /// Removes a series rule WITHOUT touching its children. Used by rule consolidation, which re-links the
    /// children to the surviving rule first; <see cref="CancelSeriesTimer"/> is the user-facing removal.
    /// </summary>
    /// <param name="ruleId">The series rule id.</param>
    public void RemoveSeriesTimer(string ruleId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_seriesTimers.RemoveAll(t => string.Equals(t.Id, ruleId, StringComparison.Ordinal)) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// Repoints every child timer of one series rule at another, so collapsing duplicate rules never strands
    /// children on a rule id clients can no longer resolve.
    /// </summary>
    /// <param name="fromRuleId">The rule losing its children.</param>
    /// <param name="toRuleId">The rule adopting them.</param>
    public void RelinkSeriesChildren(string fromRuleId, string toRuleId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var changed = false;
            foreach (var timer in _timers.Where(t => string.Equals(t.SeriesTimerId, fromRuleId, StringComparison.Ordinal)))
            {
                timer.SeriesTimerId = toRuleId;
                changed = true;
            }

            if (changed)
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// Removes child timers whose series rule no longer exists. An orphaned child keeps advertising series
    /// state for a rule id Jellyfin cannot resolve, so the client's cancel-series button 404s forever.
    /// </summary>
    /// <param name="liveRuleIds">The ids of the rules that currently exist.</param>
    public void RemoveOrphanSeriesChildren(IReadOnlyCollection<string> liveRuleIds)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_timers.RemoveAll(t => !string.IsNullOrEmpty(t.SeriesTimerId) && !liveRuleIds.Contains(t.SeriesTimerId)) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>
    /// Cancels a timer, aborting it in place: no recording is delivered, whether the window has started or not.
    /// The timer is kept as a cancelled tombstone with its original window rather than deleted, because a
    /// deleted series child would be re-created by the very next expansion pass (the airing has no timer and
    /// no recording file, so it looks unscheduled). Tombstones block re-creation until the airing has fully
    /// passed, are skipped by the recording pass, age out once the window has passed, and are replaced by a
    /// fresh timer if record is pressed again (the per-program dedupe in <see cref="SaveTimer"/>). Unknown ids
    /// are ignored: the timer may already have aged out of the store.
    /// </summary>
    /// <param name="timerId">The timer id.</param>
    public void CancelTimer(string timerId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var timer = _timers.FirstOrDefault(t => string.Equals(t.Id, timerId, StringComparison.Ordinal));
            if (timer is null)
            {
                return;
            }

            timer.Status = RecordingStatus.Cancelled;
            Persist();
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

    // Whether a status is a final outcome (fulfilled or failed by the recording service, or cancelled by the
    // user), as opposed to the clock-derived scheduled/underway states. Terminal timers are never due for
    // recording, keep their status on reads, and age out once their window has passed.
    private static bool IsTerminal(RecordingStatus status) => status is RecordingStatus.Completed or RecordingStatus.Error or RecordingStatus.Cancelled;

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
