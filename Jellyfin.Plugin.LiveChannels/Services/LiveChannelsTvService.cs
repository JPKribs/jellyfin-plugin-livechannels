using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Exposes the configured virtual channels to Jellyfin's Live TV entirely in-process, with no HTTP endpoints:
/// channels and their precomputed schedule come straight from the plugin, and each live stream is an ffmpeg
/// feed written to a local temp file that Jellyfin reads and re-exposes through its own authenticated Live TV.
/// </summary>
public sealed class LiveChannelsTvService : ILiveTvService, IDisposable
{
    // Content added within this window is treated as new (not a repeat) in the guide.
    private const int NewWindowDays = 14;

    // How long a session with no remaining viewers keeps encoding so a viewer surfing back gets an instant
    // re-tune (the warm session is adopted by the next open instead of a fresh encoder cold-starting). The trade
    // is at most this much tail encoding per channel a viewer leaves; the session cap and timeout still bound the total.
    private static readonly TimeSpan LingerGrace = TimeSpan.FromSeconds(30);

    // How long a viewer may go entirely unreported by Jellyfin's session manager before the watchdog treats it as
    // vanished and releases it. A viewer that opened a stream but cancelled before playback ever started never
    // sends a close, so its consumer would pin the session forever; a real viewer's client reports the live
    // stream id in its play state well within this window, so an id absent this long has no one behind it. Long
    // enough to ride out tune-in buffering and slow first progress reports.
    private static readonly TimeSpan ViewerAbsenceGrace = TimeSpan.FromMinutes(5);

    // Client pre-roll: the player buffers this much before playback starts, riding out tune-in jitter. The
    // reader is not realtime-paced, so this fills at I/O speed from already-produced segments -- it costs a
    // fraction of a second of wait, and adjusting it in either direction is imperceptible, so it is fixed.
    private const int BufferSeconds = 3;

    private readonly ChannelService _channels;
    private readonly StreamSessionService _streams;
    private readonly DefaultLogoService _defaultLogo;
    private readonly ActivityLogger _activity;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<LiveChannelsTvService> _logger;

    private readonly bool _probeOpenedSource;
    private readonly string _streamRoot;
    private readonly string _logoRoot = Path.Combine(Path.GetTempPath(), "livechannels-logos");

    // One encoder per channel. A channel currently being encoded has exactly one session, tracked here by channel
    // id; every viewer of that channel is a CONSUMER of that single session (see LiveSession). Jellyfin opens a
    // separate live stream per viewer, so a popular channel produces several opens/closes against one session, and
    // the session must outlive every one of its viewers -- only when the LAST viewer leaves does it linger and then
    // tear down. Serialised by _openGate so two concurrent tune-ins of one channel share a session instead of
    // racing two encoders (and possibly evicting each other).
    private readonly ConcurrentDictionary<string, LiveSession> _byChannel = new(StringComparer.Ordinal);

    // Maps each live-stream id Jellyfin holds (one per viewer) to its session, so CloseLiveStream routes a viewer's
    // close to the right session. Several ids can map to one session (one per consumer). The id the session was
    // created under is its stable display id (LiveSession.Id); adopted viewers get fresh ids that also land here.
    private readonly ConcurrentDictionary<string, LiveSession> _live = new(StringComparer.Ordinal);

    // When each currently-live consumer id was first seen with no Jellyfin playback session reporting it. An id
    // stays here only while it remains unreported; once it has been continuously absent for the grace, the
    // watchdog releases it. Reported or departed ids are pruned every sweep.
    private readonly ConcurrentDictionary<string, DateTime> _unreportedSince = new(StringComparer.Ordinal);

    private readonly object _openGate = new();
    private readonly Timer _reaper;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsTvService"/> class.
    /// </summary>
    /// <param name="channels">The channel service, used to resolve channels and their schedule.</param>
    /// <param name="streams">The stream session service, used to produce each channel's ffmpeg feed.</param>
    /// <param name="defaultLogo">The generated fallback-logo service.</param>
    /// <param name="activity">The activity logger, used to record channel start/stop in Jellyfin's activity log.</param>
    /// <param name="sessionManager">The session manager, used by the watchdog to see which live streams clients are actually playing.</param>
    /// <param name="appHost">The application host, used to read the server version the probe fix depends on.</param>
    /// <param name="appPaths">The application paths, used to default the stream directory under Jellyfin's cache.</param>
    /// <param name="logger">The logger.</param>
    public LiveChannelsTvService(ChannelService channels, StreamSessionService streams, DefaultLogoService defaultLogo, ActivityLogger activity, ISessionManager sessionManager, IServerApplicationHost appHost, IApplicationPaths appPaths, ILogger<LiveChannelsTvService> logger)
    {
        _channels = channels;
        _streams = streams;
        _defaultLogo = defaultLogo;
        _activity = activity;
        _sessionManager = sessionManager;
        _logger = logger;

        // Probing the opened source defeats Jellyfin's forced-interlaced hack (playback remuxes instead of
        // re-encoding), but from 10.11.10 the probe normalises the live playlist's container to "ts" and the
        // delivery ffmpeg is launched with `-f mpegts` against the .m3u8 — no playback at all. Only servers
        // below that version probe; newer ones keep the declared streams.
        _probeOpenedSource = appHost.ApplicationVersion < new Version(10, 11, 10);
        _logger.LogInformation(
            "Live Channels: server {Version}; opened-source probing {State}",
            appHost.ApplicationVersion,
            _probeOpenedSource ? "enabled (defeats the live-TV forced-interlace flag)" : "disabled (10.11.10+ probe breaks live HLS container detection)");

        // Where each channel's growing stream file is written (and where the schedule cache lives). Configurable,
        // since the file lives for the whole watch and the system temp is often small or RAM-backed; defaults to a
        // livechannels folder in Jellyfin's cache. Resolved by ChannelService so both agree on the same root. A
        // change takes effect on the next restart (active streams keep using the directory they started in).
        _streamRoot = ChannelService.ResolveStreamRoot(appPaths);

        // A fresh process owns no live streams, so anything left in the stream root is an orphan from a previous
        // run that ended without CloseLiveStream (a crash). Sweep it now, then periodically reap sessions whose
        // producer has stopped, or that have run past the configured time limit, but that Jellyfin never closed,
        // so neither files nor encoders pile up.
        ReapOrphanFiles();
        _reaper = new Timer(_ => ReapSessions(), state: null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public string Name => "Live Channels";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ChannelInfo>();
        foreach (var channel in _channels.GetEnabledChannels())
        {
            var info = new ChannelInfo
            {
                Id = channel.Id,
                Name = channel.Name,
                Number = channel.Number.ToString(CultureInfo.InvariantCulture),
                ChannelType = ChannelType.TV
            };

            var logo = await EnsureLogoAsync(channel, cancellationToken).ConfigureAwait(false);
            if (logo is not null)
            {
                info.ImagePath = logo;
                info.HasImage = true;
            }

            result.Add(info);
        }

        _logger.LogInformation("Live Channels: provided {Count} channel(s) to Live TV", result.Count);
        return result;
    }

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var channel = _channels.FindChannel(channelId);
        if (channel is null)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        // The guide refresh is the single build: resolve the loop and overwrite the on-disk cache so every tune-in
        // until the next refresh reads it instead of re-resolving the library on the stream's start-up path.
        var programs = _channels.RefreshPrograms(channel);
        if (programs.Count == 0)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var schedule = _channels.BuildTimeline(channel, programs, startDateUtc, endDateUtc);
        var newSince = DateTime.UtcNow.AddDays(-NewWindowDays);

        var list = new List<ProgramInfo>(schedule.Count);
        foreach (var slot in schedule)
        {
            var p = slot.Program;

            // For episodes, hand Jellyfin the series as the program name and the episode's own name as the
            // episode title, so the guide renders "Andor — Reckoning · S1E3" instead of a single blurred string.
            var isEpisode = p.SeriesId.HasValue && !string.IsNullOrWhiteSpace(p.SeriesName);
            var isNew = p.DateAdded >= newSince;
            list.Add(new ProgramInfo
            {
                Id = channelId + "_" + slot.Start.Ticks.ToString(CultureInfo.InvariantCulture),
                ChannelId = channelId,
                Name = isEpisode ? p.SeriesName! : p.Title,
                EpisodeTitle = isEpisode ? p.RawName : null,
                Overview = p.Overview,
                StartDate = slot.Start,
                EndDate = slot.Stop,
                Genres = p.Genres.ToList(),
                OfficialRating = p.OfficialRating,
                CommunityRating = p.CommunityRating,
                ProductionYear = p.Year,
                OriginalAirDate = p.PremiereDate,
                SeasonNumber = p.SeasonNumber,
                EpisodeNumber = p.EpisodeNumber,
                // SeriesId links episodes to their series; ShowId groups the repeated airings of one item in the
                // guide (the series for episodes, the item itself for movies).
                SeriesId = p.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
                ShowId = (p.SeriesId ?? p.ItemId).ToString("N", CultureInfo.InvariantCulture),
                IsSeries = p.SeriesId.HasValue,
                IsMovie = p.IsMovie,
                IsKids = _channels.KidsActiveAt(channel, slot.Start),
                IsSports = channel.Category == ChannelCategory.Sports,
                IsNews = channel.Category == ChannelCategory.News,
                IsHD = p.SourceHeight >= 720,
                IsRepeat = !isNew,
                IsPremiere = isNew,
                ImagePath = p.GuideImagePath,
                HasImage = !string.IsNullOrEmpty(p.GuideImagePath)
            });
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(list);
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = _channels.FindChannel(channelId);
        var sources = channel is null
            ? new List<MediaSourceInfo>()
            : new List<MediaSourceInfo> { BuildSource(channelId, path: null) };

        return Task.FromResult(sources);
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var channel = _channels.FindChannel(channelId)
            ?? throw new InvalidOperationException("No enabled channel matches id " + channelId);

        _logger.LogInformation("Live Channels: opening live stream for {Name}", channel.Name);

        // A channel already being encoded is ADOPTED: this open joins the existing session as another consumer
        // instead of starting a second encoder. Its segments and live edge already exist on disk, so the new open
        // is served in milliseconds, one channel never runs two encoders side by side, and -- crucially -- the
        // session now outlives every viewer, not just the most recent one. Jellyfin opens a separate live stream
        // per viewer, so two people watching one channel are two consumers of one session; it is torn down only
        // when the LAST of them closes (see CloseLiveStream). The lookup and registration are serialised so two
        // simultaneous cold tune-ins of the same channel share one session rather than racing two encoders.
        LiveSession? adopted;
        string liveStreamId;
        lock (_openGate)
        {
            if (_byChannel.TryGetValue(channelId, out var existing) && !existing.Worker.IsCompleted)
            {
                liveStreamId = NewLiveId();
                existing.AddConsumer(liveStreamId);
                _live[liveStreamId] = existing;
                adopted = existing;
            }
            else
            {
                adopted = null;
                liveStreamId = string.Empty;
            }
        }

        if (adopted is not null)
        {
            var warmPlaylist = Path.Combine(adopted.Path, "stream.m3u8");
            try
            {
                await WaitForPlaylistAsync(warmPlaylist, adopted.Worker, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Live Channels: adopted the warm session for {Name}", channel.Name);
                return BuildSource(liveStreamId, warmPlaylist);
            }
            catch (OperationCanceledException)
            {
                // The tune was abandoned mid-adoption; drop just this consumer. The session stays live for its
                // other viewers (or lingers, then reaps, if this was the last).
                ReleaseConsumer(liveStreamId);
                throw;
            }
            catch (Exception ex)
            {
                // The warm session turned out to be broken; its producer is dead, so every consumer is stuck.
                // Tear the whole session down and fall through to a fresh start.
                _logger.LogWarning(ex, "Live Channels: the warm session for {Name} was not playable; starting fresh", channel.Name);
                ReleaseConsumer(liveStreamId);
                TeardownSession(adopted);
            }
        }

        // Fresh start: a new session with its own directory, producer, and this open as its first consumer.
        LiveSession session;
        string playlist;
        lock (_openGate)
        {
            // Between releasing the lock above and re-acquiring it, another open for this channel may have won the
            // race and created the session; join it rather than starting a second encoder.
            if (_byChannel.TryGetValue(channelId, out var raced) && !raced.Worker.IsCompleted)
            {
                liveStreamId = NewLiveId();
                raced.AddConsumer(liveStreamId);
                _live[liveStreamId] = raced;
                session = raced;
                playlist = Path.Combine(raced.Path, "stream.m3u8");
            }
            else
            {
                liveStreamId = NewLiveId();

                // Each session gets its own directory holding the live playlist and its rolling segments; the whole
                // directory is removed on teardown.
                var dir = Path.Combine(_streamRoot, liveStreamId);
                Directory.CreateDirectory(dir);
                playlist = Path.Combine(dir, "stream.m3u8");

                var cts = new CancellationTokenSource();
                var stats = new SessionStats
                {
                    // Every ffmpeg the session spawns appends its command and exit summary here for the Sessions
                    // tab's log viewer; living inside the session directory, it is deleted with the session.
                    LogPath = Path.Combine(dir, "ffmpeg.log")
                };

                // Start the producer FIRST so the segmenter is already filling segments while the logo resolves
                // below; the logo is usually cached, but its first-ever generation must not sit on the tune-in
                // critical path.
                var worker = StartProducer(channel, dir, cts, stats);
                session = new LiveSession(liveStreamId, cts, worker, dir, channel.Name, channelId, channel.Number, DateTime.UtcNow, stats);
                session.AddConsumer(liveStreamId);
                _live[liveStreamId] = session;
                _byChannel[channelId] = session;

                // Bound the total number of concurrent encoders. A client that never sends the close (Swiftfin on
                // a force-quit, crash, or network drop) leaks a producer Jellyfin keeps reading, which is
                // indistinguishable from a live one until the viewer watchdog notices no client is reporting it;
                // the hard cap bounds how many such leaks can pile up in the meantime: over it, the oldest
                // sessions are closed.
                EnforceSessionCap(keep: session);
            }
        }

        // Resolve the channel logo (uploaded or generated, cached on disk, usually already written at guide time)
        // so the Sessions tab can show it without re-resolving on every poll. Not tied to the request token: a
        // client cancelling the tune must not leave the session with no logo for its whole life. A no-op when the
        // session was joined (its logo is already set).
        session.LogoPath ??= await EnsureLogoAsync(channel, CancellationToken.None).ConfigureAwait(false);

        // Jellyfin opens the playlist immediately, so wait until the segmenter has written it and a first segment,
        // and if the producer dies with nothing, drop this consumer (tearing the session down if it was the only
        // one) and surface a clear failure rather than handing over an empty playlist.
        try
        {
            await WaitForPlaylistAsync(playlist, session.Worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ReleaseConsumer(liveStreamId))
            {
                TeardownSession(session);
            }

            _logger.LogWarning(ex, "Live Channels: {Name} produced no playable output", channel.Name);
            throw;
        }

        _activity.Log(
            "Live Channel: " + channel.Name + " has started",
            "LiveChannels.ChannelStarted",
            overview: "This channel is now being encoded.");

        return BuildSource(liveStreamId, playlist);
    }

    // Starts the producer for a session: the stream session runs the HLS segmenter and feeds it, writing the live
    // playlist and its rolling segments into the session directory. The directory is removed when the session ends.
    private Task StartProducer(Channel channel, string dir, CancellationTokenSource cts, SessionStats stats)
    {
        return Task.Run(() => RunProducerAsync(() => _streams.StreamToHlsAsync(channel, dir, cts.Token, stats), channel.Name), CancellationToken.None);
    }

    // Runs a producer delegate with the standard lifecycle: swallow cancellation and log any real failure. The
    // segmenter owns its own output files, so there is nothing to dispose here.
    private async Task RunProducerAsync(Func<Task> produce, string channelName)
    {
        try
        {
            await produce().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closed by the player or by CloseLiveStream; expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live channel {Name}: stream producer failed", channelName);
        }
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        // One viewer closed. Drop it from its session; while any other viewer is still watching, the session (and
        // its single encoder) keeps running for them. Only when the LAST viewer leaves does the session linger --
        // kept warm briefly so a viewer surfing straight back adopts it (an essentially instant re-tune) -- and
        // then tear down if nobody returned.
        if (ReleaseConsumer(id, out var session) && session is not null)
        {
            _ = LingerTeardownAsync(session);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops a session immediately, bypassing the linger grace. The Sessions tab's kill button is an explicit
    /// administrator action ("free this encoder NOW"), so unlike a client close it tears the session down even if
    /// viewers are still attached.
    /// </summary>
    /// <param name="id">The session id (or any of its live stream ids) to kill.</param>
    public void KillSession(string id)
    {
        var session = Lookup(id);
        if (session is null || !TeardownSession(session))
        {
            return;
        }

        _activity.Log(
            "Live Channel: " + session.ChannelName + " has stopped",
            "LiveChannels.ChannelStopped",
            overview: "Stream stopped from the dashboard.");

        _logger.LogInformation("Live Channels: session {Id} ({Name}) killed from the dashboard", session.Id, session.ChannelName);
    }

    // Tears a session down after the grace period, unless a viewer re-attached (adopted it) in the meantime.
    private async Task LingerTeardownAsync(LiveSession session)
    {
        await Task.Delay(LingerGrace).ConfigureAwait(false);

        // A re-tune within the grace re-attached a consumer: leave the session running for them.
        if (session.HasConsumers)
        {
            return;
        }

        if (TeardownSession(session))
        {
            _activity.Log(
                "Live Channel: " + session.ChannelName + " has stopped",
                "LiveChannels.ChannelStopped",
                overview: "This channel has no viewers so encoding has been stopped.");

            _logger.LogInformation("Live Channels: closing session {Id} ({Name}) after the linger grace", session.Id, session.ChannelName);
        }
    }

    /// <summary>
    /// Snapshots every active channel stream for the configuration page's Sessions tab: its id, channel number
    /// and name, when it started (the page derives run time from this), and the latest encode speed. One row per
    /// session (encoder), not per viewer.
    /// </summary>
    /// <returns>The active sessions, ordered by channel number.</returns>
    public IReadOnlyList<ActiveSession> GetActiveSessions()
    {
        return DistinctSessions()
            .Select(s => new ActiveSession(
                s.Id,
                s.Number,
                s.ChannelName,
                s.StartedUtc,
                Math.Round(s.Stats.Speed, 2),
                StopsIn(s)))
            .OrderBy(s => s.Number)
            .ThenBy(s => s.StartedUtc)
            .ToList();
    }

    // Seconds until a viewerless session is torn down, or null while it still has a viewer. Clamped at zero: the
    // delayed teardown can run a beat behind the clock.
    private static int? StopsIn(LiveSession session)
        => session.LingeringSinceUtc is { } since
            ? (int)Math.Max(0, (LingerGrace - (DateTime.UtcNow - since)).TotalSeconds)
            : null;

    /// <summary>Returns the on-disk logo path for an active session, or <c>null</c> if the session is gone.</summary>
    /// <param name="id">The session id (or any of its live stream ids).</param>
    /// <returns>The logo file path, or <c>null</c>.</returns>
    public string? GetSessionLogoPath(string id) => Lookup(id)?.LogoPath;

    /// <summary>Returns the ffmpeg diagnostic log path for an active session, or <c>null</c> if the session is gone.</summary>
    /// <param name="id">The session id (or any of its live stream ids).</param>
    /// <returns>The log file path, or <c>null</c>.</returns>
    public string? GetSessionLogPath(string id) => Lookup(id) is { } session ? Path.Combine(session.Path, "ffmpeg.log") : null;

    /// <inheritdoc />
    public void Dispose()
    {
        _reaper.Dispose();

        // Stop and clean up every live session still open at shutdown.
        foreach (var session in DistinctSessions())
        {
            if (!session.MarkTornDown())
            {
                continue;
            }

            ForgetSession(session);
            try
            {
                session.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }

            DisposeSession(session);
        }
    }

    // Deletes stream files and concat lists left behind by a previous process that exited without closing its
    // streams. Only safe to call at construction, before this process has opened any of its own streams.
    private void ReapOrphanFiles()
    {
        try
        {
            if (Directory.Exists(_streamRoot))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(_streamRoot))
                {
                    // The schedule cache is a long-lived directory in this root, not an orphaned
                    // session, so never reap it.
                    if (IsReservedDir(sessionDir))
                    {
                        continue;
                    }

                    TryDeleteDirectory(sessionDir);
                }

                // Tidy any stray loose files, including a leftover schedule.json from an older (single-file)
                // version, which the per-channel cache replaces.
                foreach (var file in Directory.EnumerateFiles(_streamRoot))
                {
                    TryDeleteFile(file);
                }
            }

            foreach (var list in Directory.EnumerateFiles(Path.GetTempPath(), "livechannels-*.txt"))
            {
                TryDeleteFile(list);
            }

            ReapSubtitleCache();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not reap orphaned temp files");
        }
    }

    // The extracted-subtitle cache is small and reused across sessions, so keep recent entries but drop stale
    // ones and any leftover temp file from an interrupted atomic write.
    private void ReapSubtitleCache()
    {
        var subtitleRoot = Path.Combine(Path.GetTempPath(), "livechannels-subs");
        if (!Directory.Exists(subtitleRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        foreach (var file in Directory.EnumerateFiles(subtitleRoot))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal) || File.GetLastWriteTimeUtc(file) < cutoff)
            {
                TryDeleteFile(file);
            }
        }
    }

    // The periodic reaper: collects sessions whose producer has finished, releases viewers no client is actually
    // playing, then closes any still-running session that has passed the configured time limit. All are streams
    // Jellyfin never closed, so without this they would hold their encoder and files for the life of the process.
    private void ReapSessions()
    {
        ReapCompletedSessions();
        ReapAbandonedConsumers();

        // Safety net: a viewerless session's delayed teardown normally collects it, but if that task was ever
        // lost, this sweep makes sure a viewerless encoder cannot run forever.
        foreach (var session in DistinctSessions())
        {
            if (session.LingeringSinceUtc is { } since && DateTime.UtcNow - since > LingerGrace * 3
                && !session.HasConsumers && TeardownSession(session))
            {
                _logger.LogWarning("Live Channels: reaping over-lingered session {Id} ({Name})", session.Id, session.ChannelName);
            }
        }

        var timeoutMinutes = Plugin.Instance?.ReadConfiguration(c => c.SessionTimeoutMinutes) ?? 0;
        if (timeoutMinutes <= 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(timeoutMinutes);
        foreach (var session in DistinctSessions())
        {
            if (session.StartedUtc <= cutoff && TeardownSession(session))
            {
                _logger.LogInformation("Live Channels: closing session {Id} ({Name}) after reaching the {Minutes}-minute time limit", session.Id, session.ChannelName, timeoutMinutes);
            }
        }
    }

    // Reaps sessions whose producer task has already finished (the stream ended or failed) but that Jellyfin
    // never closed, so the CancellationTokenSource and the temp file do not linger for the life of the process.
    private void ReapCompletedSessions()
    {
        foreach (var session in DistinctSessions())
        {
            if (session.Worker.IsCompleted && TeardownSession(session))
            {
                _logger.LogDebug("Live Channels: reaping finished session {Id} ({Name})", session.Id, session.ChannelName);
            }
        }
    }

    // The viewer watchdog: releases consumers no client is actually playing. A viewer that opens a stream but
    // cancels before playback starts (or force-quits mid-buffer) never sends a close, so its consumer pins the
    // session forever; the session cap only bounds how MANY such leaks accumulate, not how long one lives. Every
    // playing client reports its live stream id (our consumer id behind Jellyfin's service prefix) in its play
    // state, so a consumer id no session has reported for the whole grace has no viewer behind it. Releasing it
    // routes through the same path as a real close: while other viewers remain the session keeps running, and
    // when the last is released the session lingers and tears down normally.
    private void ReapAbandonedConsumers()
    {
        List<string> reported;
        try
        {
            reported = new List<string>();
            foreach (var jellyfinSession in _sessionManager.Sessions)
            {
                var state = jellyfinSession.PlayState;
                if (state is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(state.LiveStreamId))
                {
                    reported.Add(state.LiveStreamId);
                }

                if (!string.IsNullOrEmpty(state.MediaSourceId))
                {
                    reported.Add(state.MediaSourceId);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a session-manager hiccup take down the reaper timer; skip this sweep.
            _logger.LogDebug(ex, "Live Channels: could not snapshot playback sessions for the viewer watchdog");
            return;
        }

        var live = _live.Keys.ToList();
        var unwatched = SelectUnwatchedConsumers(live, reported);
        var unwatchedSet = new HashSet<string>(unwatched, StringComparer.Ordinal);

        // A consumer that is reported again (or gone) resets: only CONTINUOUS absence for the grace counts, so a
        // slow tune-in that starts reporting late is never reaped.
        foreach (var id in _unreportedSince.Keys)
        {
            if (!unwatchedSet.Contains(id))
            {
                _unreportedSince.TryRemove(id, out _);
            }
        }

        var now = DateTime.UtcNow;
        foreach (var id in unwatched)
        {
            var since = _unreportedSince.GetOrAdd(id, now);
            if (now - since < ViewerAbsenceGrace)
            {
                continue;
            }

            _unreportedSince.TryRemove(id, out _);
            if (_live.TryGetValue(id, out var orphaned))
            {
                _logger.LogWarning(
                    "Live Channels: no client has reported viewer {Id} of {Name} for {Minutes} minute(s); releasing it",
                    id,
                    orphaned.ChannelName,
                    (int)ViewerAbsenceGrace.TotalMinutes);
            }

            if (ReleaseConsumer(id, out var session) && session is not null)
            {
                _ = LingerTeardownAsync(session);
            }
        }
    }

    /// <summary>
    /// Selects the consumer ids no playback session references. A client playing one of our streams reports a
    /// live stream id of the form <c>{servicePrefix}_{consumerId}</c> (and sometimes the bare id as its media
    /// source id), so a consumer is watched when any reported id contains it. Pure and deterministic so it can be
    /// unit tested without a live host.
    /// </summary>
    /// <param name="consumerIds">Every live consumer id (one per viewer).</param>
    /// <param name="reportedIds">Every live-stream and media-source id reported by active playback sessions.</param>
    /// <returns>The consumer ids no reported id references.</returns>
    public static List<string> SelectUnwatchedConsumers(IEnumerable<string> consumerIds, IEnumerable<string> reportedIds)
    {
        var reported = reportedIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        return consumerIds
            .Where(consumer => !reported.Any(id => id.Contains(consumer, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Reaps finished sessions, then deletes any stream file in the stream directory that is not tied to a live
    /// session. Safe to run at any time: a file an active session is still writing is skipped, so it can never
    /// interrupt playback. Backs the scheduled cleanup task and the manual "run now" in Scheduled Tasks.
    /// </summary>
    /// <returns>The number of orphaned stream files removed.</returns>
    public int CleanupOrphanStreams()
    {
        ReapCompletedSessions();

        var active = new HashSet<string>(DistinctSessions().Select(s => s.Path), StringComparer.Ordinal);
        var removed = 0;
        try
        {
            if (Directory.Exists(_streamRoot))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(_streamRoot))
                {
                    // Never delete the schedule cache directory; it is not a session.
                    if (active.Contains(sessionDir) || IsReservedDir(sessionDir))
                    {
                        continue;
                    }

                    TryDeleteDirectory(sessionDir);
                    if (!Directory.Exists(sessionDir))
                    {
                        removed++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: stream cleanup sweep failed");
        }

        _logger.LogInformation("Live Channels: stream cleanup removed {Count} orphaned stream directory(ies)", removed);
        return removed;
    }

    // Bounds the number of concurrent encoders to the configured cap by closing the oldest sessions. A client that
    // never sends the close leaks a producer Jellyfin keeps reading off disk; the viewer watchdog reaps it after
    // its grace, and this hard count is the immediate bound until it does. The just-opened session is always kept.
    // Zero is unlimited. Called while holding _openGate, so the session set it reads is stable.
    private void EnforceSessionCap(LiveSession keep)
    {
        var cap = Plugin.Instance?.ReadConfiguration(c => c.MaxConcurrentSessions) ?? 0;
        if (cap <= 0)
        {
            return;
        }

        var sessions = DistinctSessions();
        var victims = SelectCapVictims(sessions.Select(s => (s.Id, s.StartedUtc)), keep.Id, cap);
        foreach (var id in victims)
        {
            var victim = sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (victim is not null && TeardownSession(victim))
            {
                _logger.LogInformation("Live Channels: closing session {Id} ({Name}) to stay within the {Cap}-session cap", victim.Id, victim.ChannelName, cap);
            }
        }
    }

    /// <summary>
    /// Chooses which sessions to close so the live count fits the cap. Returns the oldest sessions first, never the
    /// one just opened (<paramref name="keep"/>), and only as many as the overflow above the cap. A cap of zero or
    /// less is unlimited and selects nothing. Pure and deterministic so it can be unit tested without a live host.
    /// </summary>
    /// <param name="sessions">Every live session as an id and its start time.</param>
    /// <param name="keep">The just-opened session that must never be evicted.</param>
    /// <param name="cap">The maximum number of concurrent sessions; zero or less means unlimited.</param>
    /// <returns>The ids to close, oldest first.</returns>
    public static List<string> SelectCapVictims(IEnumerable<(string Id, DateTime StartedUtc)> sessions, string keep, int cap)
    {
        var all = sessions.ToList();
        var overflow = all.Count - cap;
        if (cap <= 0 || overflow <= 0)
        {
            return new List<string>();
        }

        return all
            .Where(s => !string.Equals(s.Id, keep, StringComparison.Ordinal))
            .OrderBy(s => s.StartedUtc)
            .Take(overflow)
            .Select(s => s.Id)
            .ToList();
    }

    // The distinct sessions currently tracked (one per active channel), plus any transiently still routed in
    // _live. Reference-distinct, so a session reachable by several consumer ids appears once.
    private List<LiveSession> DistinctSessions()
        => _byChannel.Values.Concat(_live.Values).Distinct().ToList();

    // Finds a session by its stable session id or by any of its live stream (consumer) ids.
    private LiveSession? Lookup(string id)
    {
        if (_live.TryGetValue(id, out var byConsumer))
        {
            return byConsumer;
        }

        return _byChannel.Values.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    // Drops one viewer from its session. Returns whether the session now has no viewers left (so the caller can
    // linger or tear it down). The out overload also returns the session the id belonged to.
    private bool ReleaseConsumer(string id) => ReleaseConsumer(id, out _);

    private bool ReleaseConsumer(string id, out LiveSession? session)
    {
        if (_live.TryRemove(id, out var found))
        {
            session = found;
            return found.RemoveConsumer(id);
        }

        session = null;
        return false;
    }

    // Removes a session from every tracking map so no new open can adopt it and no close can route to it. Idempotent
    // via the session's torn-down flag; returns whether THIS call performed the teardown (so the caller logs once).
    private bool TeardownSession(LiveSession session)
    {
        if (!session.MarkTornDown())
        {
            return false;
        }

        ForgetSession(session);
        CancelAndDispose(session);
        return true;
    }

    // Removes every map entry pointing at a session (its channel slot and any consumer ids), without disposing it.
    private void ForgetSession(LiveSession session)
    {
        _byChannel.TryRemove(new KeyValuePair<string, LiveSession>(session.ChannelId, session));
        foreach (var kv in _live)
        {
            if (ReferenceEquals(kv.Value, session))
            {
                _live.TryRemove(new KeyValuePair<string, LiveSession>(kv.Key, session));
            }
        }
    }

    // Cancels a session's producer and removes its directory once the producer has fully stopped (its segmenter is
    // killed in StreamToHlsAsync's finally), so the recursive delete cannot race the segmenter still writing
    // segments into it. Fire-and-forget so the caller (a tune-in or the reaper) is never blocked draining the old one.
    private void CancelAndDispose(LiveSession session)
    {
        try
        {
            session.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        _ = session.Worker.ContinueWith(_ => DisposeSession(session), TaskScheduler.Default);
    }

    private void DisposeSession(LiveSession session)
    {
        try
        {
            session.Cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        TryDeleteDirectory(session.Path);

        // This session has been forgotten by the caller, so if no other session is watching this channel, drop its
        // decoded schedule from memory. The on-disk cache remains for the next tune-in.
        ReleaseScheduleIfIdle(session.Number);
    }

    // Releases a channel's in-memory schedule when it has no remaining live sessions, so an unwatched channel holds
    // nothing. Called after a session has already been forgotten.
    private void ReleaseScheduleIfIdle(int channelNumber)
    {
        if (!_byChannel.Values.Any(s => s.Number == channelNumber))
        {
            _channels.ReleaseFromMemory(channelNumber);
        }
    }

    // Whether a directory under the stream root is a long-lived cache (the per-channel schedule files) rather
    // than a session, which the orphan and cleanup sweeps must never delete.
    private static bool IsReservedDir(string path)
        => string.Equals(Path.GetFileName(path), ChannelService.ScheduleDirName, StringComparison.Ordinal);

    private static string NewLiveId() => "lc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private void TryDeleteFile(string path)
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

    // Removes a session directory and everything in it (the playlist plus its segments).
    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not delete stream directory {Path}", path);
        }
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    // The plugin provides no DVR; the timer surface is stubbed so Jellyfin's Live TV never errors calling it.

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    // Builds the live HLS source descriptor. With no path it is the "menu" entry Jellyfin opens via
    // GetChannelStream; with a path (the .m3u8) it is the opened stream Jellyfin reads and later closes by its Id.
    // We produce the stream ourselves at a known resolution/codec, so we advertise concrete video/audio
    // streams (and skip probing): without them, a client that needs transcoding crashes Jellyfin's scale
    // filter on the null source dimensions.
    private MediaSourceInfo BuildSource(string id, string? path)
    {
        var (width, bitrateKbps, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);
        var height = (int)Math.Round(width * 9.0 / 16.0);

        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = videoCodec == Models.VideoCodec.Hevc ? "hevc" : "h264",
            Width = width,
            Height = height,
            RealFrameRate = 30,
            AverageFrameRate = 30,
            BitRate = bitrateKbps * 1000,
            IsInterlaced = false,
            PixelFormat = "yuv420p"
        };

        var audio = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = audioCodec switch
            {
                Models.AudioCodec.Ac3 => "ac3",
                Models.AudioCodec.Eac3 => "eac3",
                _ => "aac"
            },
            Channels = 2,
            SampleRate = 48000
        };

        // The OPENED source (path set) must be probed by Jellyfin, counter-intuitively: after GetChannelStream
        // returns, Jellyfin's live-TV provider force-flags every provided video stream as interlaced (a legacy
        // "make clients deinterlace" hack: IsInterlaced=true, NalLengthSize "0", DisplayTitle "1080i"), which
        // makes clients re-encode the whole stream with "interlaced video is not supported". The open-stream
        // probe runs AFTER that hack and its fresh result replaces the doctored streams, so the playback decision
        // sees the output as it really is (progressive) and direct-streams. The handover already waited for the
        // first segments, so the probe has real content to read. The unopened "menu" source (no path) cannot be
        // probed, so it keeps the declared streams: without them a client that needs transcoding crashes
        // Jellyfin's scale filter on null source dimensions.
        //
        // VERSION CEILING: from Jellyfin 10.11.10 the probe normalises a live HLS playlist's container to its
        // SEGMENT container ("ts" instead of "hls"), and the delivery ffmpeg is then launched with `-f mpegts`
        // pointed at the .m3u8 -- which cannot parse, exits 187 on every attempt, and kills ALL playback. On
        // those servers the opened source keeps the declared streams instead (see _probeOpenedSource).
        var opened = path is not null && _probeOpenedSource;
        return new MediaSourceInfo
        {
            Id = id,
            Path = path,
            Protocol = MediaProtocol.File,
            Container = "hls",
            IsInfiniteStream = true,
            // Deliberately NOT ReadAtNativeFramerate: the live playlist (no end tag) already paces Jellyfin's
            // reader to segment availability at the live edge. Adding -re on top caps it at 1x realtime even when
            // it is behind, so the client buffer could only ever fill at realtime (slow tune-in) and a reader that
            // hiccuped could never catch back up before falling off the back of the delete window.
            // Pre-roll a few seconds on the client so playback starts on a full buffer instead of stuttering
            // while it fills (the declarative form of pausing briefly then resuming on tune-in).
            BufferMs = BufferSeconds * 1000,
            RequiresOpening = path is null,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsProbing = opened,
            // Keep the probe snappy: our TS declares its PAT/PMT and interleaves both streams within
            // milliseconds, and the handover guarantees ~8s of segments exist, so one second of analysis is
            // ample and every tune-in saves the difference.
            AnalyzeDurationMs = opened ? 1000 : null,
            MediaStreams = opened ? Array.Empty<MediaStream>() : new[] { video, audio }
        };
    }

    private static async Task WaitForPlaylistAsync(string playlist, Task worker, CancellationToken cancellationToken)
    {
        // Hand Jellyfin the playlist only once the segmenter has written it and buffered a couple of segments, so
        // playback starts on a small cushion (the per-item path spawns a fresh ffmpeg per item, and these
        // buffered segments ride over the brief gap between items). The initial burst fills them fast, and keeps
        // filling well past this gate while Jellyfin spins up its own repackager, so two segments (8s) is enough
        // to hand over on without risking an under-run.
        const int MinSegments = 2;
        var dir = Path.GetDirectoryName(playlist) ?? string.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !worker.IsCompleted)
        {
            if (File.Exists(playlist) && CountSegments(dir) >= MinSegments)
            {
                return;
            }

            // Let cancellation propagate (do not swallow it): a client that drops the tune-in mid-wait must reach
            // the caller's teardown, otherwise the producer keeps encoding into a session nobody will ever close.
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        // The loop ended because the deadline passed or the producer finished. A deadline hit with SOME segments
        // means a slow encoder still filling: hand the playlist over, it will keep growing. But with no playlist
        // or no segments at all there is nothing Jellyfin can play — whether the producer finished (it made
        // nothing) or is still "running" (it is stuck, e.g. on an unreachable mount): fail loudly so the caller
        // tears the session down, instead of handing over an empty playlist that dies later with a cryptic
        // probe error.
        if (!File.Exists(playlist) || CountSegments(dir) < 1)
        {
            throw new InvalidOperationException("The channel produced no playable output.");
        }
    }

    // Counts the current HLS segments in a session directory (the rolling window the segmenter keeps on disk).
    // Enumerates rather than materialising an array: this runs in the 200ms tune-in poll loop.
    private static int CountSegments(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ts").Count() : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Writes (once) the channel's logo to disk and returns its path, so it can be set as ChannelInfo.ImagePath
    // with no HTTP endpoint: the uploaded image when present, otherwise the generated square.
    private async Task<string?> EnsureLogoAsync(Channel channel, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_logoRoot);
            var number = channel.Number.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(channel.LogoData))
            {
                var file = Path.Combine(_logoRoot, number + "-" + Hash(channel.LogoData) + "." + ExtensionFor(channel.LogoContentType));
                if (!File.Exists(file))
                {
                    await File.WriteAllBytesAsync(file, Convert.FromBase64String(channel.LogoData), cancellationToken).ConfigureAwait(false);
                }

                return file;
            }

            var token = string.Join('|', channel.Name, ((int)channel.LogoStyle).ToString(CultureInfo.InvariantCulture), channel.LogoSymbol, channel.LogoShowName ? "1" : "0");
            var generated = Path.Combine(_logoRoot, number + "-g6-" + Hash(token) + ".png");
            if (!File.Exists(generated))
            {
                var bytes = await _defaultLogo.GetAsync(channel.Number, channel.Name, channel.LogoStyle, channel.LogoSymbol, channel.LogoShowName, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    return null;
                }

                await File.WriteAllBytesAsync(generated, bytes, cancellationToken).ConfigureAwait(false);
            }

            return generated;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not prepare a logo for channel {Name}", channel.Name);
            return null;
        }
    }

    private static string ExtensionFor(string contentType)
        => contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpeg",
            "image/jpg" => "jpeg",
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "png"
        };

    private static string Hash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    // One channel's live encoder and the viewers attached to it. A session is created on the first tune-in of a
    // channel and shared by every later tune-in of the same channel (each is a consumer); it lives until its last
    // consumer leaves (then a linger grace), so no viewer can tear it down while another is still watching. The
    // consumer set and linger state are guarded by a lock so opens and closes on different threads stay consistent.
    private sealed class LiveSession
    {
        private readonly object _gate = new();
        private readonly List<string> _consumers = new();
        private int _tornDown;

        public LiveSession(string id, CancellationTokenSource cts, Task worker, string path, string channelName, string channelId, int number, DateTime startedUtc, SessionStats stats)
        {
            Id = id;
            Cts = cts;
            Worker = worker;
            Path = path;
            ChannelName = channelName;
            ChannelId = channelId;
            Number = number;
            StartedUtc = startedUtc;
            Stats = stats;
        }

        // Stable display id (the live stream id the session was created under), used by the Sessions tab and the
        // kill/log lookups. It never changes for the session's life, even after that first consumer closes.
        public string Id { get; }

        public CancellationTokenSource Cts { get; }

        public Task Worker { get; }

        public string Path { get; }

        public string ChannelName { get; }

        public string ChannelId { get; }

        public int Number { get; }

        public DateTime StartedUtc { get; }

        public SessionStats Stats { get; }

        public string? LogoPath { get; set; }

        // When the last consumer left (the session is warm, awaiting teardown), or null while a viewer is attached.
        public DateTime? LingeringSinceUtc { get; private set; }

        public bool HasConsumers
        {
            get
            {
                lock (_gate)
                {
                    return _consumers.Count > 0;
                }
            }
        }

        // Attaches a viewer and clears any pending linger (a re-tune within the grace keeps the session alive).
        public void AddConsumer(string id)
        {
            lock (_gate)
            {
                if (!_consumers.Contains(id))
                {
                    _consumers.Add(id);
                }

                LingeringSinceUtc = null;
            }
        }

        // Detaches a viewer; returns whether none remain (so the caller lingers or tears the session down).
        public bool RemoveConsumer(string id)
        {
            lock (_gate)
            {
                _consumers.Remove(id);
                if (_consumers.Count == 0)
                {
                    LingeringSinceUtc = DateTime.UtcNow;
                    return true;
                }

                return false;
            }
        }

        // Claims the teardown exactly once, so several triggers (last-viewer linger, the reaper, the cap, dispose)
        // cannot dispose the same session twice.
        public bool MarkTornDown() => Interlocked.Exchange(ref _tornDown, 1) == 0;
    }
}
