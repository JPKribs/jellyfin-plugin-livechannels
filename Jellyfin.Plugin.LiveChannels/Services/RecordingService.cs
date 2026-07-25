using System;
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
public sealed partial class RecordingService
{
    // How far ahead series rules are expanded into child timers. Far enough that the upcoming list is useful,
    // small enough that a looping channel (where the same series airs over and over) cannot flood the store.
    private static readonly TimeSpan SeriesLookahead = TimeSpan.FromHours(24);

    // At most this many new child timers per rule per pass; with one distinct episode per pass each, this
    // bounds the store's growth no matter how dense the loop is.
    private const int MaxChildrenPerRulePass = 16;

    // A materialization that keeps failing (source missing, recording folder unwritable) is retried on later
    // passes up to this many attempts, then marked errored so it stops occupying the due list.
    private const int MaxMaterializeAttempts = 3;

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
    private int _running;

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
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunPassAsync);
    }

    // The interesting failure here is per-timer and handled inside; this catch only guards the pass itself
    // (e.g. an unreadable livetv configuration) so a fault can never kill the heartbeat.
    private async Task RunPassAsync()
    {
        try
        {
            var options = ReadLiveTvOptions();
            var defaultRoot = DefaultRecordingRoot();
            ConsolidateSeriesRules();
            ExpandSeriesRules(options, defaultRoot);
            await MaterializeDueAsync(options, defaultRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: the recording maintenance pass failed; it will run again on the next heartbeat");
        }
        finally
        {
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
            // Rules whose anchor no longer resolves keep their own identity rather than collapsing blindly.
            var anchor = Resolve(rule.ProgramId);
            var key = anchor is null
                ? "rule:" + rule.Id
                : anchor.ChannelId + ":" + (anchor.Slot.Program.SeriesId ?? anchor.Slot.Program.ItemId).ToString("N", CultureInfo.InvariantCulture);
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
    // distinct episode per pass, skipping programs that already have a timer or an already-materialized file,
    // so re-running is idempotent and a looping channel does not re-record the same episode forever.
    private void ExpandSeriesRules(LiveTvOptions? options, string defaultRoot)
    {
        foreach (var rule in _timers.GetSeriesTimers())
        {
            var anchor = Resolve(rule.ProgramId);
            if (anchor is null)
            {
                _logger.LogDebug("Live Channels: series rule {Id} anchor program no longer resolves; skipping expansion this pass", rule.Id);
                continue;
            }

            var seriesKey = anchor.Slot.Program.SeriesId ?? anchor.Slot.Program.ItemId;
            var now = DateTime.UtcNow;
            var timeline = _channels.BuildTimeline(anchor.Channel, _channels.ResolvePrograms(anchor.Channel), now, now + SeriesLookahead);

            var created = 0;
            var seenItems = new HashSet<Guid>();
            foreach (var slot in timeline)
            {
                var program = slot.Program;
                if ((program.SeriesId ?? program.ItemId) != seriesKey || !MatchesRule(rule, slot.Start) || !seenItems.Add(program.ItemId))
                {
                    continue;
                }

                var programId = anchor.ChannelId + "_" + slot.Start.Ticks.ToString(CultureInfo.InvariantCulture);
                if (_timers.HasTimerForProgram(programId) || File.Exists(BuildTarget(_fileSystem, options, defaultRoot, program, slot.Start, SourceExtension(program)).File))
                {
                    continue;
                }

                _timers.SaveTimer(BuildChildTimer(rule, anchor.ChannelId, programId, slot));
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

    // Produces the recording for every timer whose window has ended. Failures are retried on later passes; a
    // timer that keeps failing is marked errored so the due list cannot grow without bound.
    private async Task MaterializeDueAsync(LiveTvOptions? options, string defaultRoot)
    {
        foreach (var timer in _timers.TimersDueForRecording())
        {
            try
            {
                await MaterializeAsync(timer, options, defaultRoot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                timer.RetryCount++;
                if (timer.RetryCount >= MaxMaterializeAttempts)
                {
                    _timers.CompleteTimer(timer.Id, RecordingStatus.Error, recordingPath: null);
                    _logger.LogWarning(ex, "Live Channels: could not produce the recording for \"{Name}\"; giving up after {Attempts} attempts", timer.Name, timer.RetryCount);
                }
                else
                {
                    _timers.SaveTimer(timer);
                    _logger.LogWarning(ex, "Live Channels: could not produce the recording for \"{Name}\" (attempt {Attempt}); it will be retried", timer.Name, timer.RetryCount);
                }
            }
        }
    }

    private async Task MaterializeAsync(TimerInfo timer, LiveTvOptions? options, string defaultRoot)
    {
        var resolved = Resolve(timer.ProgramId)
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
            _timers.CompleteTimer(timer.Id, RecordingStatus.Completed, target.File);
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
            // mid-copy never leaves a half-written file that scans as a corrupt recording.
            var temp = target.File + ".copying.tmp";
            try
            {
                var openOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
                var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, openOptions);
                await using (src.ConfigureAwait(false))
                {
                    var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, openOptions);
                    await using (dst.ConfigureAwait(false))
                    {
                        await src.CopyToAsync(dst).ConfigureAwait(false);
                    }
                }

                File.Move(temp, target.File, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        _timers.CompleteTimer(timer.Id, RecordingStatus.Completed, target.File);
        _logger.LogInformation("Live Channels: recorded \"{Name}\" to {Path} ({Method})", timer.Name, target.File, linked ? "hardlink" : "copy");
        _activity.Log(
            "Live Channel recording: " + (string.IsNullOrEmpty(timer.Name) ? program.Title : timer.Name),
            "LiveChannels.RecordingCompleted",
            overview: "Saved to " + target.File);

        await EnsureRecordingLibraryAsync(target).ConfigureAwait(false);
        TriggerRefresh(target.File);
    }

    // Resolves a guide program id ("<channelId>_<startTicks>") back to its channel and scheduled slot. The
    // schedule is deterministic (epoch-anchored), so past and future windows both resolve; if the schedule has
    // drifted since the timer was created (durations healed, channel edited), the slot now covering the
    // original start stands in.
    private ResolvedProgram? Resolve(string? programId)
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

        var programs = _channels.ResolvePrograms(channel);
        if (programs.Count == 0)
        {
            return null;
        }

        var start = new DateTime(parsed.Value.StartTicks, DateTimeKind.Utc);
        var timeline = _channels.BuildTimeline(channel, programs, start, start.AddMinutes(1));
        var slot = timeline.FirstOrDefault(s => s.Start.Ticks == parsed.Value.StartTicks)
            ?? timeline.FirstOrDefault(s => s.Start <= start && start < s.Stop);
        return slot is null ? null : new ResolvedProgram(channel, parsed.Value.ChannelId, slot);
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

    private static string DateStamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);

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

    // Hardlinks source to dest when the platform and volume allow it, so a same-volume recording costs no
    // space. Any failure (cross-volume above all) simply reports false and the caller copies.
    private bool TryHardLink(string source, string dest)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? NativeMethods.CreateHardLinkW(dest, source, IntPtr.Zero)
                : NativeMethods.Link(source, dest) == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: hardlink from {Source} to {Dest} unavailable; copying instead", source, dest);
            return false;
        }
    }

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

        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateHardLinkW(string newPath, string existingPath, IntPtr securityAttributes);
    }
}
