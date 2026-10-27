using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Fulfils DVR timers. Jellyfin no longer asks Live TV services for recordings: a recording IS a media file in
/// the Live TV recording folders, which the server exposes as an ordinary library and scans like any other
/// folder (the built-in DVR works the same way). The channels replay library content that already exists on
/// disk, so fulfilment needs no capture or transcode: when a timer's window ends, the program is resolved back
/// to the library item it aired (the guide was built from those items, and the program id encodes channel and
/// start), and that item's file is hardlinked (same volume) or copied (otherwise) into the recording folder
/// under the built-in DVR's naming scheme, then the recordings library is refreshed so it appears immediately.
/// The same pass expands series rules into per-airing child timers, which is what lets clients see and cancel
/// series state (Jellyfin surfaces a program's series link only via a child timer pointing at the rule).
/// Driven by the live TV service's once-a-minute heartbeat plus a kick after each series create.
/// </summary>
public sealed partial class RecordingService : IDisposable
{
    // How far ahead series rules are expanded into child timers. Far enough that the upcoming list is useful,
    // small enough that a looping channel (where the same series airs over and over) cannot flood the store.
    private static readonly TimeSpan SeriesLookahead = TimeSpan.FromHours(24);

    // At most this many new child timers per rule per pass; with one distinct episode per pass each, this
    // bounds the store's growth no matter how dense the loop is.
    private const int MaxChildrenPerRulePass = 16;

    // A materialization that keeps failing (source missing, recording folder unwritable, a NAS briefly
    // unmounted) is retried on later passes with a growing delay between attempts, and only marked errored once
    // it has failed this long past its window, so a transient outage does not turn into a permanent error.
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    // Suffix of the temp file a cross-volume copy writes before moving it into place.
    private const string CopyTempSuffix = ".copying.tmp";

    private readonly ChannelService _channels;
    private readonly TimerService _timers;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _config;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly ActivityLogger _activity;
    private readonly ILogger<RecordingService> _logger;

    // Non-reentrancy gate for the maintenance pass: the heartbeat and the post-create kick may overlap, and a
    // long copy must not stack a second pass behind it.
    private readonly ConcurrentDictionary<int, (long Generation, IReadOnlyList<ProgramEntry> Programs)> _passPrograms = new();

    // Set only on the recording pass's own flow (see ProgramsFor).
    private readonly AsyncLocal<bool> _inPass = new();
    private int _running;

    // Cancelled at shutdown so a long copy stops instead of holding the server up (its temp file is removed).
    private readonly CancellationTokenSource _shutdown = new();

    // When each failing timer may next be retried. Only touched by the pass, which is non-reentrant.
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.Ordinal);

    // Whether the stale-copy sweep has run this process; it runs once, on the first pass, before any copy.
    private bool _sweptCopies;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingService"/> class.
    /// </summary>
    /// <param name="channels">The channel service, used to resolve programs back to their library items.</param>
    /// <param name="timers">The timer store the pass reads due timers from and writes outcomes to.</param>
    /// <param name="libraryManager">The library manager, used to find and register the recordings library.</param>
    /// <param name="providerManager">The provider manager, used to refresh the recordings library after a file lands.</param>
    /// <param name="config">The server configuration manager, used to read the Live TV recording folder settings.</param>
    /// <param name="fileSystem">The file system, used for path comparison and filename sanitisation.</param>
    /// <param name="appPaths">The application paths, used for the default recording folder.</param>
    /// <param name="activity">The activity logger, used to record completed recordings in Jellyfin's activity log.</param>
    /// <param name="logger">The logger.</param>
    public RecordingService(ChannelService channels, TimerService timers, ILibraryManager libraryManager, IProviderManager providerManager, IServerConfigurationManager config, IFileSystem fileSystem, IApplicationPaths appPaths, ActivityLogger activity, ILogger<RecordingService> logger)
    {
        _channels = channels;
        _timers = timers;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _config = config;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>
    /// Schedules a maintenance pass (series expansion + due materializations) if one is not already running.
    /// Fire-and-forget by design: callers are the reaper heartbeat and the create path, neither of which may
    /// block on library IO.
    /// </summary>
    public void Kick()
    {
        if (_shutdown.IsCancellationRequested || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunPassAsync);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    // The interesting failure here is per-timer and handled inside; this catch only guards the pass itself
    // (e.g. an unreadable livetv configuration) so a fault can never kill the heartbeat.
    private async Task RunPassAsync()
    {
        _passPrograms.Clear();
        _inPass.Value = true;
        try
        {
            var token = _shutdown.Token;
            var options = ReadLiveTvOptions();
            var defaultRoot = DefaultRecordingRoot();
            if (!_sweptCopies)
            {
                _sweptCopies = true;
                SweepStaleCopies(options, defaultRoot);
            }

            ConsolidateSeriesRules();
            ExpandSeriesRules(options, defaultRoot);
            await MaterializeDueAsync(options, defaultRoot, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && _shutdown.IsCancellationRequested)
        {
            // Shutting down; the interrupted timer is still due and is retried on the next start.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: the recording maintenance pass failed; it will run again on the next heartbeat");
        }
        finally
        {
            _inPass.Value = false;
            _passPrograms.Clear();
            Volatile.Write(ref _running, 0);
        }
    }

    // Collapses duplicate series rules down to one per series per channel. Record-series pressed from two
    // different airings of the same show creates two rules with different anchor programs, and the per-program
    // dedupe cannot see they target the same series; left alone they double-record every airing, and the
    // children of replaced rules dangle off rule ids clients can no longer resolve (the cancel button then
    // 404s forever). The newest rule wins, the older rules' children are re-linked to it, and any children
    // still pointing at rules that no longer exist at all are removed.
    private void ConsolidateSeriesRules()
    {
        var rules = _timers.GetSeriesTimers();
        var byKey = new Dictionary<string, List<SeriesTimerInfo>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            // Rules whose series is not known keep their own identity rather than collapsing blindly.
            var target = RuleTarget(rule);
            var key = target is null
                ? "rule:" + rule.Id
                : target.Value.ChannelId + ":" + target.Value.Key.ToString("N", CultureInfo.InvariantCulture);
            if (!byKey.TryGetValue(key, out var group))
            {
                byKey[key] = group = new List<SeriesTimerInfo>();
            }

            group.Add(rule);
        }

        foreach (var group in byKey.Values.Where(g => g.Count > 1))
        {
            // Store order is insertion order, so the last rule is the user's most recent press.
            var keeper = group[^1];
            foreach (var duplicate in group.Take(group.Count - 1))
            {
                _timers.RelinkSeriesChildren(duplicate.Id, keeper.Id);
                _timers.RemoveSeriesTimer(duplicate.Id);
            }

            _logger.LogInformation("Live Channels: consolidated {Count} duplicate series rules for \"{Name}\"", group.Count, keeper.Name);
        }

        _timers.RemoveOrphanSeriesChildren(_timers.GetSeriesTimers().Select(r => r.Id).ToList());
    }

    // Expands each series rule into concrete child timers for upcoming airings of its series on its channel,
    // linked back via SeriesTimerId (the link clients need to show and cancel series state). One airing per
    // distinct episode, skipping items the rule has already recorded, had cancelled, or holds a child for, and
    // programs that already have a timer or an already-materialized file, so re-running is idempotent and a
    // looping channel does not re-record the same episode forever.
    private void ExpandSeriesRules(LiveTvOptions? options, string defaultRoot)
    {
        foreach (var rule in _timers.GetSeriesTimers())
        {
            var target = RuleTarget(rule);
            if (target is not { } resolved || _channels.FindChannel(resolved.ChannelId) is not { } channel)
            {
                _logger.LogDebug("Live Channels: series rule {Id} has no resolvable series or channel; skipping expansion this pass", rule.Id);
                continue;
            }

            var seriesKey = resolved.Key;
            var now = DateTime.UtcNow;
            var timeline = _channels.BuildTimeline(channel, ProgramsFor(channel), now, now + SeriesLookahead);

            var created = 0;
            var seenItems = new HashSet<Guid>();
            foreach (var slot in timeline)
            {
                var program = slot.Program;
                if ((program.SeriesId ?? program.ItemId) != seriesKey || !MatchesRule(rule, slot.Start) || (rule.RecordNewOnly && !program.IsNewAt(now)) || !seenItems.Add(program.ItemId)
                    || _timers.IsSeriesItemTaken(rule.Id, program.ItemId))
                {
                    continue;
                }

                var programId = resolved.ChannelId + "_" + slot.Start.Ticks.ToString(CultureInfo.InvariantCulture);
                if (_timers.HasTimerForProgram(programId) || File.Exists(BuildTarget(_fileSystem, options, defaultRoot, program, slot.Start, SourceExtension(program)).File))
                {
                    continue;
                }

                var child = BuildChildTimer(rule, resolved.ChannelId, programId, slot);
                SetScheduledItem(child, program.ItemId);
                _timers.SaveTimer(child);
                if (++created >= MaxChildrenPerRulePass)
                {
                    break;
                }
            }

            if (created > 0)
            {
                _logger.LogInformation("Live Channels: series rule \"{Name}\" scheduled {Count} upcoming airing(s)", rule.Name, created);
            }
        }
    }

    // Whether an airing satisfies the rule's day and time-of-day constraints. Day matching uses local time
    // (the terms users think in); the any-time window is generous because a looping schedule rarely repeats
    // at exact wall-clock times.
    private static bool MatchesRule(SeriesTimerInfo rule, DateTime slotStartUtc)
    {
        if (rule.Days is { Count: > 0 } days && !days.Contains(slotStartUtc.ToLocalTime().DayOfWeek))
        {
            return false;
        }

        if (rule.RecordAnyTime)
        {
            return true;
        }

        var diff = Math.Abs((slotStartUtc.TimeOfDay - rule.StartDate.TimeOfDay).TotalHours);
        return Math.Min(diff, 24 - diff) <= 2;
    }

    private static TimerInfo BuildChildTimer(SeriesTimerInfo rule, string channelId, string programId, ScheduledProgram slot)
    {
        var program = slot.Program;
        var isEpisode = program.SeriesId.HasValue && !string.IsNullOrWhiteSpace(program.SeriesName);
        return new TimerInfo
        {
            ChannelId = channelId,
            ProgramId = programId,
            SeriesTimerId = rule.Id,
            Name = isEpisode ? program.SeriesName! : program.Title,
            EpisodeTitle = isEpisode ? program.RawName : null,
            Overview = program.Overview,
            StartDate = slot.Start,
            EndDate = slot.Stop,
            PrePaddingSeconds = rule.PrePaddingSeconds,
            PostPaddingSeconds = rule.PostPaddingSeconds,
            Priority = rule.Priority,
            SeasonNumber = program.SeasonNumber,
            EpisodeNumber = program.EpisodeNumber,
            IsMovie = program.IsMovie,
            IsSeries = isEpisode,
            IsProgramSeries = isEpisode,
            ProductionYear = program.Year,
            OriginalAirDate = program.PremiereDate,
            Status = RecordingStatus.New
        };
    }

    // Produces the recording for every timer whose window has ended. Failures are retried on later passes with a
    // growing delay; a timer still failing well past its window is marked errored so the due list cannot grow
    // without bound.
    private async Task MaterializeDueAsync(LiveTvOptions? options, string defaultRoot, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = _timers.TimersDueForRecording();
        foreach (var stale in _retryAfter.Keys.Where(id => !due.Any(t => string.Equals(t.Id, id, StringComparison.Ordinal))).ToList())
        {
            _retryAfter.Remove(stale);
        }

        foreach (var timer in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_retryAfter.TryGetValue(timer.Id, out var after) && now < after)
            {
                continue;
            }

            try
            {
                await MaterializeAsync(timer, options, defaultRoot, cancellationToken).ConfigureAwait(false);
                _retryAfter.Remove(timer.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Counted on the stored timer, not this instance: a client edit made meanwhile must not be lost.
                if (_timers.RecordFailedAttempt(timer.Id) is not { } attempts)
                {
                    _retryAfter.Remove(timer.Id);
                    continue;
                }

                if (now - timer.EndDate >= GiveUpAfter)
                {
                    _retryAfter.Remove(timer.Id);
                    _timers.CompleteTimer(timer.Id, RecordingStatus.Error, recordingPath: null);
                    _logger.LogWarning(ex, "Live Channels: could not produce the recording for \"{Name}\"; giving up after {Attempts} attempts", timer.Name, attempts);
                }
                else
                {
                    var delay = RetryDelay(attempts);
                    _retryAfter[timer.Id] = now + delay;
                    _logger.LogWarning(ex, "Live Channels: could not produce the recording for \"{Name}\" (attempt {Attempt}); retrying in {Minutes} minute(s)", timer.Name, attempts, (int)delay.TotalMinutes);
                }
            }
        }
    }

    /// <summary>
    /// The delay before retrying a recording that has failed the given number of times: one minute, doubling per
    /// failure, capped at <see cref="MaxRetryDelay"/>.
    /// </summary>
    /// <param name="attempts">The failed attempts so far (1 or more).</param>
    /// <returns>The delay before the next attempt.</returns>
    internal static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 16));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxRetryDelay.TotalMinutes));
    }

    private async Task MaterializeAsync(TimerInfo timer, LiveTvOptions? options, string defaultRoot, CancellationToken cancellationToken)
    {
        var resolved = Resolve(timer.ProgramId, ScheduledItem(timer))
            ?? throw new InvalidOperationException("The program no longer resolves to a scheduled library item.");
        var program = resolved.Slot.Program;
        var source = program.Path;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            throw new InvalidOperationException($"The backing library file is missing: {source ?? "(no path)"}");
        }

        var target = BuildTarget(_fileSystem, options, defaultRoot, program, resolved.Slot.Start, Path.GetExtension(source));
        var linked = false;
        if (File.Exists(target.File))
        {
            // Already produced (an earlier pass crashed between the file landing and the outcome persisting,
            // or a repeat airing of the same episode): adopt it rather than duplicating.
            // Not this rule's own recording (it may be a one-off recording of the same episode), so it is never
            // counted toward, or pruned by, the rule's "keep up to".
            _timers.CompleteTimer(timer.Id, RecordingStatus.Completed, target.File, adopted: true);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target.File)!);
        if (TryHardLink(source, target.File))
        {
            linked = true;
        }
        else
        {
            // Cross-volume (or a filesystem without hardlinks): copy through a temp name and move, so a crash
            // mid-copy never leaves a half-written file that scans as a corrupt recording. A copy interrupted by
            // shutdown is abandoned and its temp file removed; one interrupted by a crash is swept on the next start.
            var temp = target.File + CopyTempSuffix;
            try
            {
                // Plain using blocks: the copy itself is async, and a FileStream's synchronous Dispose after
                // the awaited copy completes flushes the same way (the await-using ConfigureAwait form read as
                // an undisposed stream to static analysis).
                var openOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, openOptions))
                using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, openOptions))
                {
                    await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temp, target.File, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        if (!_timers.CompleteTimer(timer.Id, RecordingStatus.Completed, target.File))
        {
            // Cancelled while the file was being produced: the cancel stands. The file is a finished recording of
            // the library item, so it is left for the user rather than deleted.
            _logger.LogInformation("Live Channels: \"{Name}\" was cancelled while its recording was produced at {Path}; the timer stays cancelled", timer.Name, target.File);
            return;
        }

        _logger.LogInformation("Live Channels: recorded \"{Name}\" to {Path} ({Method})", timer.Name, target.File, linked ? "hardlink" : "copy");
        _activity.Log(
            "Live Channel recording: " + (string.IsNullOrEmpty(timer.Name) ? program.Title : timer.Name),
            "LiveChannels.RecordingCompleted",
            overview: "Saved to " + target.File);

        await EnsureRecordingLibraryAsync(target).ConfigureAwait(false);
        TriggerRefresh(target.File);
        EnforceKeepUpTo(timer);
    }

    // Applies a series rule's "keep up to" limit after it records: its own oldest recordings beyond the limit are
    // deleted, as Jellyfin's built-in DVR does. Only files this rule produced are touched; a recording is a link to
    // (or copy of) a library file, so the library item itself is never affected.
    private void EnforceKeepUpTo(TimerInfo timer)
    {
        if (string.IsNullOrEmpty(timer.SeriesTimerId)
            || _timers.GetSeriesTimers().FirstOrDefault(r => string.Equals(r.Id, timer.SeriesTimerId, StringComparison.Ordinal)) is not { KeepUpTo: > 0 } rule)
        {
            return;
        }

        foreach (var old in _timers.TakeExcessRecordings(rule.Id, rule.KeepUpTo))
        {
            if (!File.Exists(old))
            {
                continue;
            }

            try
            {
                File.Delete(old);
                _logger.LogInformation("Live Channels: series rule \"{Name}\" keeps {Keep} recording(s); removed the oldest, {Path}", rule.Name, rule.KeepUpTo, old);
                TriggerRefresh(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Live Channels: could not remove the old recording {Path} for series rule \"{Name}\"", old, rule.Name);
            }
        }
    }

    /// <summary>
    /// The <see cref="TimerInfo.ProviderIds"/> key a timer carries the library item it was scheduled for under, so
    /// a later reschedule (durations healed, channel edited) cannot make it record whatever now airs in its slot.
    /// </summary>
    internal const string ScheduledItemKey = "LiveChannelsItem";

    // A channel's loop, read once per pass and shared by every rule and timer the pass handles. Not pinned in the
    // channel service's memory (a channel nobody watches holds nothing between passes), and not re-read for each
    // rule. Only the pass's own flow uses the cache: a timer or rule created from the guide meanwhile (on a request
    // thread) reads the current loop, so it is never stamped from a loop a guide refresh has since replaced. A save
    // that clears the schedules mid-pass also retires what the pass cached.
    private IReadOnlyList<ProgramEntry> ProgramsFor(Channel channel)
    {
        if (!_inPass.Value)
        {
            return _channels.ResolvePrograms(channel, keepInMemory: false);
        }

        var generation = ChannelService.CacheGeneration;
        if (_passPrograms.TryGetValue(channel.Number, out var cached) && cached.Generation == generation)
        {
            return cached.Programs;
        }

        var programs = _channels.ResolvePrograms(channel, keepInMemory: false);
        _passPrograms[channel.Number] = (generation, programs);
        return programs;
    }

    /// <summary>
    /// Stamps a series rule with the series it records (the item itself for a movie), unless it already carries
    /// one. Called when the rule is created, the moment the guide entry the user pressed record on is still
    /// current. The rule's anchor program id is a past slot that a later schedule rebuild can fill with another
    /// item, so the rule must not keep re-deriving its series from it.
    /// </summary>
    /// <param name="rule">The series rule to stamp.</param>
    public void StampSeriesKey(SeriesTimerInfo rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (RuleKey(rule) is not null)
        {
            return;
        }

        try
        {
            if (Resolve(rule.ProgramId) is { } resolved)
            {
                rule.SeriesId = KeyOf(resolved.Slot.Program).ToString("N", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            // Unstamped, the rule is matched by name on the next pass (see RuleTarget).
            _logger.LogDebug(ex, "Live Channels: could not resolve the series for rule \"{Name}\"", rule.Name);
        }
    }

    /// <summary>Gets the series (or movie item) a rule is stamped with, or <c>null</c> when it carries none.</summary>
    /// <param name="rule">The series rule.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    internal static Guid? RuleKey(SeriesTimerInfo rule)
        => Guid.TryParse(rule.SeriesId, out var key) && key != Guid.Empty ? key : null;

    /// <summary>The key a series rule matches airings by: the series for an episode, the item itself otherwise.</summary>
    /// <param name="program">The program.</param>
    /// <returns>The key.</returns>
    internal static Guid KeyOf(ProgramEntry program) => program.SeriesId ?? program.ItemId;

    /// <summary>
    /// Works out the series a rule created before rules were stamped was meant for. Its anchor slot is used only
    /// while it still airs a show of the rule's name (the name was copied from the guide entry the user pressed);
    /// otherwise the show of that name on the channel. <c>null</c> when the name is ambiguous or not on the channel.
    /// </summary>
    /// <param name="ruleName">The rule's name.</param>
    /// <param name="anchorProgram">What the anchor slot airs now, or <c>null</c>.</param>
    /// <param name="programs">The channel's programs.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    internal static Guid? LegacyRuleKey(string? ruleName, ProgramEntry? anchorProgram, IReadOnlyList<ProgramEntry> programs)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return null;
        }

        bool Named(ProgramEntry p) => string.Equals(GuideName(p), ruleName, StringComparison.OrdinalIgnoreCase);
        if (anchorProgram is not null && Named(anchorProgram))
        {
            return KeyOf(anchorProgram);
        }

        var keys = programs.Where(Named).Select(KeyOf).Distinct().Take(2).ToList();
        return keys.Count == 1 ? keys[0] : null;
    }

    // The name the guide gives a program, which a rule created from it copies: the series for an episode.
    private static string GuideName(ProgramEntry program)
        => program.SeriesId.HasValue && !string.IsNullOrWhiteSpace(program.SeriesName) ? program.SeriesName! : program.Title;

    // The channel and series a rule records. A stamped rule needs nothing but its program id's channel; an
    // unstamped (older) one is matched once by name and then stamped, so it stops depending on its anchor slot.
    private (string ChannelId, Guid Key)? RuleTarget(SeriesTimerInfo rule)
    {
        var parsed = ParseProgramId(rule.ProgramId);
        var channelId = parsed?.ChannelId ?? rule.ChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            return null;
        }

        if (RuleKey(rule) is { } key)
        {
            return (channelId, key);
        }

        var channel = _channels.FindChannel(channelId);
        if (channel is null)
        {
            return null;
        }

        var programs = ProgramsFor(channel);
        var anchor = Resolve(rule.ProgramId);
        if (LegacyRuleKey(rule.Name, anchor?.Slot.Program, programs) is not { } found)
        {
            return null;
        }

        _timers.SetSeriesKey(rule.Id, found.ToString("N", CultureInfo.InvariantCulture));
        _logger.LogInformation("Live Channels: series rule \"{Name}\" is now pinned to its series", rule.Name);
        return (channelId, found);
    }

    /// <summary>
    /// Stamps a timer with the library item its program airs right now, unless it already carries one. Called when
    /// a timer is created, which is the moment the guide entry the user pressed record on is still current.
    /// </summary>
    /// <param name="timer">The timer to stamp.</param>
    public void StampScheduledItem(TimerInfo timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        if (ScheduledItem(timer) is not null)
        {
            return;
        }

        try
        {
            if (Resolve(timer.ProgramId) is { } resolved)
            {
                SetScheduledItem(timer, resolved.Slot.Program.ItemId);
            }
        }
        catch (Exception ex)
        {
            // Unstamped, the timer still resolves by its slot the way older timers do.
            _logger.LogDebug(ex, "Live Channels: could not resolve the scheduled item for timer \"{Name}\"", timer.Name);
        }
    }

    /// <summary>Gets the library item a timer was scheduled for, or <c>null</c> for a timer that predates the stamp.</summary>
    /// <param name="timer">The timer.</param>
    /// <returns>The item id, or <c>null</c>.</returns>
    internal static Guid? ScheduledItem(TimerInfo timer)
        => timer.ProviderIds is { } ids && ids.TryGetValue(ScheduledItemKey, out var raw) && Guid.TryParse(raw, out var id) ? id : null;

    /// <summary>Records the library item a timer was scheduled for.</summary>
    /// <param name="timer">The timer.</param>
    /// <param name="itemId">The item id.</param>
    internal static void SetScheduledItem(TimerInfo timer, Guid itemId)
    {
        timer.ProviderIds ??= new Dictionary<string, string>(StringComparer.Ordinal);
        timer.ProviderIds[ScheduledItemKey] = itemId.ToString("N", CultureInfo.InvariantCulture);
    }

    // Resolves a guide program id ("<channelId>_<startTicks>") back to its channel and the slot that time holds
    // in the CURRENT schedule. A rebuild can put a different item into a past slot, so callers that must know
    // what was scheduled carry their own stamp (the timer's item, the rule's series) rather than trusting it. When the timer
    // carries the item it was scheduled for, that item is what resolves, even if a reschedule since (durations
    // healed, channel edited) moved something else into the slot; see PickSlot.
    private ResolvedProgram? Resolve(string? programId, Guid? scheduledItem = null)
    {
        var parsed = ParseProgramId(programId);
        if (parsed is null)
        {
            return null;
        }

        var channel = _channels.FindChannel(parsed.Value.ChannelId);
        if (channel is null)
        {
            return null;
        }

        var programs = ProgramsFor(channel);
        if (programs.Count == 0)
        {
            return null;
        }

        var start = new DateTime(parsed.Value.StartTicks, DateTimeKind.Utc);
        var timeline = _channels.BuildTimeline(channel, programs, start, start.AddMinutes(1));
        var slot = PickSlot(timeline, programs, start, scheduledItem, out var fellBack);
        if (fellBack && slot is not null)
        {
            _logger.LogWarning(
                "Live Channels: program {ProgramId} was scheduled for item {Expected}, which is no longer on the channel; recording \"{Title}\" now in that slot instead",
                programId,
                scheduledItem,
                slot.Program.Title);
        }

        return slot is null ? null : new ResolvedProgram(channel, parsed.Value.ChannelId, slot);
    }

    /// <summary>
    /// Picks the slot a timer records. Without a scheduled item (a timer from before the stamp existed) it is the
    /// slot starting exactly at the program's start, else the one covering it. With one, the slot is used only
    /// when it still airs that item; otherwise the item itself is placed at the original start (a recording is
    /// the item's file, so where it airs now does not matter). Only when the item has left the channel entirely
    /// does it fall back to the slot, reporting that through <paramref name="fellBack"/>.
    /// </summary>
    /// <param name="timeline">The channel's timeline around the start.</param>
    /// <param name="programs">The channel's current program list.</param>
    /// <param name="start">The program's original start.</param>
    /// <param name="scheduledItem">The item the timer was scheduled for, or <c>null</c>.</param>
    /// <param name="fellBack">Whether the scheduled item could not be found and the slot's item stood in.</param>
    /// <returns>The slot to record, or <c>null</c> when nothing resolves.</returns>
    internal static ScheduledProgram? PickSlot(IReadOnlyList<ScheduledProgram> timeline, IReadOnlyList<ProgramEntry> programs, DateTime start, Guid? scheduledItem, out bool fellBack)
    {
        fellBack = false;
        var slot = timeline.FirstOrDefault(s => s.Start.Ticks == start.Ticks)
            ?? timeline.FirstOrDefault(s => s.Start <= start && start < s.Stop);
        if (scheduledItem is not { } expected || slot?.Program.ItemId == expected)
        {
            return slot;
        }

        var item = programs.FirstOrDefault(p => p.ItemId == expected);
        if (item is not null)
        {
            var lead = TimeSpan.FromTicks(ProgramLoop.LeadOf(programs));
            return new ScheduledProgram(item, start, start + TimeSpan.FromTicks(item.DurationTicks) + lead) { Lead = lead };
        }

        fellBack = slot is not null;
        return slot;
    }

    /// <summary>
    /// Splits a guide program id into its channel id and slot start ticks, or <c>null</c> when malformed.
    /// </summary>
    /// <param name="programId">The program id (<c>&lt;channelId&gt;_&lt;startTicks&gt;</c>).</param>
    /// <returns>The parsed parts, or <c>null</c>.</returns>
    internal static (string ChannelId, long StartTicks)? ParseProgramId(string? programId)
    {
        if (string.IsNullOrEmpty(programId))
        {
            return null;
        }

        var sep = programId.LastIndexOf('_');
        if (sep <= 0 || sep == programId.Length - 1)
        {
            return null;
        }

        return long.TryParse(programId[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? (programId[..sep], ticks)
            : null;
    }

    /// <summary>
    /// Builds the destination for a recording, mirroring the built-in DVR's folder scheme so the file lands in
    /// the same libraries: the configured series/movie recording paths when set, otherwise the recording root
    /// with optional Series/Movies subfolders, then a DVR-style name the recordings library groups correctly
    /// (series folder, season folder, <c>Name SxxEyy Episode</c> file).
    /// </summary>
    /// <param name="fileSystem">The file system, for filename sanitisation and path comparison.</param>
    /// <param name="options">The server's Live TV options, or <c>null</c> when unreadable.</param>
    /// <param name="defaultRoot">The default recording root (used when no custom path applies).</param>
    /// <param name="program">The resolved program entry.</param>
    /// <param name="slotStartUtc">The airing's start, used in names when episode numbering is missing.</param>
    /// <param name="sourceExtension">The source file's extension (the recording keeps the source container).</param>
    /// <returns>The destination file plus the library root it belongs to.</returns>
    internal static RecordingTarget BuildTarget(IFileSystem fileSystem, LiveTvOptions? options, string defaultRoot, ProgramEntry program, DateTime slotStartUtc, string sourceExtension)
    {
        string Valid(string name) => fileSystem.GetValidFilename(name).Trim().TrimEnd('.').Trim();

        var isEpisode = program.SeriesId.HasValue && !string.IsNullOrWhiteSpace(program.SeriesName);
        var root = defaultRoot;
        var libraryName = "Recordings";
        CollectionTypeOptions? collectionType = null;
        var useSubfolder = true;

        var custom = isEpisode ? options?.SeriesRecordingPath : (program.IsMovie ? options?.MovieRecordingPath : null);
        if (!string.IsNullOrWhiteSpace(custom) && !fileSystem.AreEqual(custom, defaultRoot))
        {
            root = custom;
            useSubfolder = false;
            libraryName = isEpisode ? "Recorded Shows" : "Recorded Movies";
            collectionType = isEpisode ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies;
        }

        var dir = root;
        if (useSubfolder && (options?.EnableRecordingSubfolders ?? false))
        {
            dir = Path.Combine(dir, isEpisode ? "Series" : (program.IsMovie ? "Movies" : "Other"));
        }

        if (isEpisode)
        {
            dir = Path.Combine(dir, Valid(program.SeriesName!));
            if (program.SeasonNumber is { } season)
            {
                dir = Path.Combine(dir, "Season " + season.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            dir = Path.Combine(dir, Valid(RecordingNameFor(program, slotStartUtc)));
        }

        var file = Valid(RecordingNameFor(program, slotStartUtc)) + sourceExtension;
        return new RecordingTarget(Path.Combine(dir, file), root, libraryName, collectionType);
    }

    /// <summary>
    /// The DVR-style display name for a recording: <c>Series SxxEyy Episode</c> when numbered, a start-time
    /// stamp when not (so repeat airings cannot collide), and <c>Movie (Year)</c> for movies.
    /// </summary>
    /// <param name="program">The resolved program entry.</param>
    /// <param name="slotStartUtc">The airing's start.</param>
    /// <returns>The recording name, before filename sanitisation.</returns>
    internal static string RecordingNameFor(ProgramEntry program, DateTime slotStartUtc)
    {
        if (program.SeriesId.HasValue && !string.IsNullOrWhiteSpace(program.SeriesName))
        {
            if (program.SeasonNumber is { } season && program.EpisodeNumber is { } episode)
            {
                var name = string.Format(CultureInfo.InvariantCulture, "{0} S{1:00}E{2:00}", program.SeriesName, season, episode);
                return string.IsNullOrWhiteSpace(program.RawName) ? name : name + " " + program.RawName;
            }

            var stamped = program.SeriesName + " " + DateStamp(slotStartUtc);
            return string.IsNullOrWhiteSpace(program.RawName) ? stamped : stamped + " - " + program.RawName;
        }

        if (program.IsMovie)
        {
            return program.Year is { } year
                ? program.Title + " (" + year.ToString(CultureInfo.InvariantCulture) + ")"
                : program.Title;
        }

        return program.Title + " " + DateStamp(slotStartUtc);
    }

    private static string DateStamp(DateTime utc) => DateStamp(utc, TimeZoneInfo.Local);

    /// <summary>
    /// The local-time stamp used in recording names. Inside the repeated hour when clocks fall back, the same
    /// local time occurs twice, so the UTC offset is appended there to keep the two airings' names apart
    /// (otherwise the second would adopt the first's file as already recorded). Every other time is unchanged.
    /// </summary>
    /// <param name="utc">The airing's start (treated as UTC unless it is marked local).</param>
    /// <param name="zone">The time zone to stamp in.</param>
    /// <returns>The stamp.</returns>
    internal static string DateStamp(DateTime utc, TimeZoneInfo zone)
    {
        var instant = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        var stamp = local.ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
        if (!zone.IsAmbiguousTime(local))
        {
            return stamp;
        }

        var offset = zone.GetUtcOffset(instant);
        return stamp + "_UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hhmm", CultureInfo.InvariantCulture);
    }

    private static string SourceExtension(ProgramEntry program)
        => string.IsNullOrEmpty(program.Path) ? ".ts" : Path.GetExtension(program.Path);

    // Reads the server's Live TV options (recording paths and subfolder preference). Null when the section is
    // unavailable; every consumer falls back to the defaults the built-in DVR uses.
    private LiveTvOptions? ReadLiveTvOptions()
    {
        try
        {
            return _config.GetConfiguration<LiveTvOptions>("livetv");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not read the Live TV configuration; using default recording paths");
            return null;
        }
    }

    private string DefaultRecordingRoot()
    {
        var configured = ReadLiveTvOptions()?.RecordingPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_appPaths.DataPath, "livetv", "recordings")
            : configured;
    }

    // Registers the recording folder as a library if nothing serves it yet, mirroring the built-in DVR's
    // CreateRecordingFolders: on a box that has never recorded, the folder is not part of any library, and a
    // file in an unwatched folder is a recording that never appears.
    private async Task EnsureRecordingLibraryAsync(RecordingTarget target)
    {
        try
        {
            var known = _libraryManager.GetVirtualFolders().SelectMany(v => v.Locations);
            if (known.Any(location => _fileSystem.AreEqual(location, target.LibraryRoot)))
            {
                return;
            }

            var libraryOptions = new LibraryOptions { PathInfos = new[] { new MediaPathInfo(target.LibraryRoot) } };
            await _libraryManager.AddVirtualFolder(target.LibraryName, target.CollectionType, libraryOptions, refreshLibrary: true).ConfigureAwait(false);
            _logger.LogInformation("Live Channels: registered the recording folder {Path} as the {Name} library", target.LibraryRoot, target.LibraryName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not register the recording folder {Path} as a library; the recording exists but will not appear until the folder is added to a library", target.LibraryRoot);
        }
    }

    // Queues a refresh on the nearest library ancestor of the new file, exactly as the built-in DVR does after
    // a recording completes, so the recording appears immediately instead of at the next scheduled scan.
    private void TriggerRefresh(string recordingPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(recordingPath);
            var item = FindNearestLibraryItem(directory);
            if (item is null)
            {
                return;
            }

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                RefreshPaths = new[]
                {
                    recordingPath,
                    directory!,
                    Path.GetDirectoryName(directory)!
                }
            };
            _providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not queue a refresh for {Path}; the recording appears at the next library scan", recordingPath);
        }
    }

    private BaseItem? FindNearestLibraryItem(string? path)
    {
        while (!string.IsNullOrEmpty(path))
        {
            var item = _libraryManager.FindByPath(path, isFolder: true);
            if (item is not null)
            {
                return item;
            }

            path = Path.GetDirectoryName(path);
        }

        return null;
    }

    // Sweeps temp files left in the recording folders by a copy that a crash interrupted. Runs once per process,
    // on the first pass, before that pass starts any copy of its own, so every temp file found is an orphan.
    private void SweepStaleCopies(LiveTvOptions? options, string defaultRoot)
    {
        var roots = new[] { defaultRoot, options?.SeriesRecordingPath, options?.MovieRecordingPath }
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Distinct(StringComparer.Ordinal);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root!, "*" + CopyTempSuffix, enumeration))
                {
                    _logger.LogInformation("Live Channels: removing the interrupted recording copy {Path}", file);
                    TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live Channels: could not sweep interrupted copies under {Path}", root);
            }
        }
    }

    // Hardlinks source to dest when the platform and volume allow it, so a same-volume recording costs no
    // space. Any failure (cross-volume above all) simply reports false and the caller copies; the OS error is
    // logged so an unexpected one (permissions, a filesystem without links) can be told apart from cross-volume.
    private bool TryHardLink(string source, string dest)
    {
        try
        {
            if (OperatingSystem.IsWindows() ? NativeMethods.CreateHardLinkW(dest, source, IntPtr.Zero) : PosixLink(source, dest) == 0)
            {
                return true;
            }

            _logger.LogDebug("Live Channels: hardlink from {Source} to {Dest} failed (OS error {Error}); copying instead", source, dest, Marshal.GetLastPInvokeError());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: hardlink from {Source} to {Dest} unavailable; copying instead", source, dest);
            return false;
        }
    }

    // link(2) through the C library. On glibc Linux the bare name "libc" resolves to libc.so, which is absent or
    // a linker script on a stock install, so the versioned soname is tried first there; anywhere it does not
    // exist (macOS, musl), the bare name is used. The binding that worked is remembered.
    private static int PosixLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsLinux() && Volatile.Read(ref _glibcMissing) == 0)
        {
            try
            {
                return NativeMethods.LinkGlibc(existingPath, newPath);
            }
            catch (DllNotFoundException)
            {
                Volatile.Write(ref _glibcMissing, 1);
            }
        }

        return NativeMethods.Link(existingPath, newPath);
    }

    private static int _glibcMissing;

    private void TryDelete(string path)
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
            _logger.LogDebug(ex, "Live Channels: could not delete temp file {Path}", path);
        }
    }

    /// <summary>
    /// A recording's destination: the file to produce plus the library root (and its registration identity)
    /// the file belongs to.
    /// </summary>
    /// <param name="File">The destination file path.</param>
    /// <param name="LibraryRoot">The recording root the file lives under.</param>
    /// <param name="LibraryName">The library name to register the root as when no library serves it.</param>
    /// <param name="CollectionType">The library collection type, or <c>null</c> for the mixed default.</param>
    internal sealed record RecordingTarget(string File, string LibraryRoot, string LibraryName, CollectionTypeOptions? CollectionType);

    private sealed record ResolvedProgram(Channel Channel, string ChannelId, ScheduledProgram Slot);

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int Link(string existingPath, string newPath);

        [LibraryImport("libc.so.6", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int LinkGlibc(string existingPath, string newPath);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateHardLinkW(string newPath, string existingPath, IntPtr securityAttributes);
    }
}
