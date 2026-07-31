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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Exposes the configured virtual channels to Jellyfin's Live TV entirely in-process, with no HTTP endpoints of
/// its own: channels and their precomputed schedule come straight from the plugin, and each live stream is an
/// ffmpeg feed segmented to disk and served back through Jellyfin's own internal live stream endpoint (see
/// <see cref="DirectLiveStream"/>), the same delivery route native tuner streams use.
/// </summary>
public sealed class LiveChannelsTvService : ILiveTvService, ISupportsNewTimerIds, ISupportsDirectStreamProvider, IDisposable
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

    // A session is "watched" when Jellyfin's session manager reports one of its viewers as playing. That is the
    // authoritative answer -- it is a real playback session belonging to a real user, and it keeps reporting while
    // the player is paused, which is when a viewer is most likely to be sitting in front of a stream that is
    // sending nothing. A watched session is never shut down by the cap, the time limit, or the viewerless linger,
    // so only an explicit administrator kill (or server shutdown) can take a stream away from someone.
    //
    // Recent delivery is the second opinion, for the gap the first one has: some clients never report a live
    // stream id at all (the watchdog's grace below exists for exactly that), and a stream that is still pushing
    // bytes to a reader has someone on the other end whatever the session list says. A client that force-quit,
    // crashed, or dropped off the network cannot keep pulling, so this cannot be faked -- it can only go quiet,
    // which is why it corroborates rather than decides.
    private static readonly TimeSpan RecentDeliveryWindow = TimeSpan.FromMinutes(2);

    // How long after a producer dies unexpectedly before the session may start a replacement, and how many
    // replacements one session may run. A producer that keeps dying is a broken pipeline, not a hiccup: the
    // interval keeps a restart loop from spinning, and the cap eventually lets the session be collected instead
    // of restarting forever.
    private static readonly TimeSpan RestartInterval = TimeSpan.FromSeconds(5);
    private const int MaxProducerRestarts = 10;

    private readonly ChannelService _channels;
    private readonly StreamSessionService _streams;
    private readonly DefaultLogoService _defaultLogo;
    private readonly ActivityLogger _activity;
    private readonly TimerService _timers;
    private readonly RecordingService _recordings;
    private readonly StressTestService _stress;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<LiveChannelsTvService> _logger;

    private readonly IServerApplicationHost _appHost;
    private readonly string _streamRoot;
    private readonly string _logoRoot;

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
    /// <param name="timers">The timer store, backing the DVR timer surface.</param>
    /// <param name="recordings">The recording service, which fulfils timers by materializing their recordings.</param>
    /// <param name="stress">The stress test, cancelled when a real viewer tunes in.</param>
    /// <param name="sessionManager">The session manager, used by the watchdog to see which live streams clients are actually playing.</param>
    /// <param name="appHost">The application host, used to build the internal live stream endpoint URL each opened source points at.</param>
    /// <param name="appPaths">The application paths, used to default the stream directory under Jellyfin's cache.</param>
    /// <param name="logger">The logger.</param>
    public LiveChannelsTvService(ChannelService channels, StreamSessionService streams, DefaultLogoService defaultLogo, ActivityLogger activity, TimerService timers, RecordingService recordings, StressTestService stress, ISessionManager sessionManager, IServerApplicationHost appHost, IApplicationPaths appPaths, ILogger<LiveChannelsTvService> logger)
    {
        _channels = channels;
        _streams = streams;
        _defaultLogo = defaultLogo;
        _activity = activity;
        _timers = timers;
        _recordings = recordings;
        _stress = stress;
        _sessionManager = sessionManager;
        _logger = logger;

        // Logos live in the plugin's own cache directory, not the shared system temp: a fixed path in a
        // world-writable temp dir can be pre-created or swapped by another local user, and temp cleaners
        // delete it out from under the guide.
        _logoRoot = Path.Combine(appPaths.CachePath, "livechannels-assets", "logos");

        // Opened streams are served as a direct stream provider: each one points at Jellyfin's own
        // /LiveTv/LiveStreamFiles endpoint (a continuous MPEG-TS backed by DirectLiveStream.GetStream), which
        // Jellyfin probes like any native tuner stream. The probe result replaces the interlace flag Jellyfin
        // force-sets on plugin streams (so playback direct-streams instead of re-encoding), and with no HLS
        // playlist in the path, the 10.11.10+ probe container normalisation has nothing to break.
        _appHost = appHost;

        // Where each channel's growing stream file is written (and where the schedule cache lives). Configurable,
        // since the file lives for the whole watch and the system temp is often small or RAM-backed; defaults to a
        // livechannels folder in Jellyfin's cache. Resolved by ChannelService so both agree on the same root. A
        // change takes effect on the next restart (active streams keep using the directory they started in).
        _streamRoot = ChannelService.ResolveStreamRoot(appPaths);

        // A fresh process owns no live streams, so anything left in the stream root is an orphan from a previous
        // run that ended without CloseLiveStream (a crash). Sweep it now, then periodically reap sessions whose
        // producer has stopped, or that have run past the configured time limit, but that Jellyfin never closed,
        // so neither files nor encoders pile up. The same heartbeat drives the DVR: expiring timers get their
        // recordings materialized and series rules expand into upcoming child timers.
        EnsureStreamRootMarker();
        ReapOrphanFiles();
        _reaper = new Timer(
            _ =>
            {
                ReapSessions();
                _recordings.Kick();
            },
            state: null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
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
                // Every artwork shape the item has, each in its own slot: Jellyfin's guide builder maps ImagePath
                // to Primary and the three *ImageUrl fields to Thumb, Backdrop, and Logo (they are stored as
                // ItemImageInfo paths, and a path that does not start with "http" is treated as a local file, so
                // these never hit the network). Clients that lay out posters and clients that lay out landscape
                // cards then both render the right shape instead of one image stretched into the other.
                ImagePath = p.PrimaryImagePath,
                ThumbImageUrl = p.ThumbImagePath,
                BackdropImageUrl = p.BackdropImagePath,
                LogoImageUrl = p.LogoImagePath,
                HasImage = !string.IsNullOrEmpty(p.PrimaryImagePath) || !string.IsNullOrEmpty(p.ThumbImagePath)
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
            : new List<MediaSourceInfo> { BuildMenuSource(channelId) };

        return Task.FromResult(sources);
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        // Jellyfin's live-TV provider always prefers GetChannelStreamWithDirectStreamProvider when the service
        // implements it, so this is only a safety net for any caller still on the plain interface.
        var live = await GetChannelStreamWithDirectStreamProvider(channelId, streamId, new List<ILiveStream>(), cancellationToken).ConfigureAwait(false);
        return live.MediaSource;
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var (session, liveStreamId) = await OpenConsumerAsync(channelId, cancellationToken).ConfigureAwait(false);

        // Handing Jellyfin our own ILiveStream (instead of a MediaSourceInfo it wraps itself) keeps the plugin in
        // control of delivery: the opened source's path is Jellyfin's internal live stream endpoint, which serves
        // DirectLiveStream.GetStream — the session's segments as one continuous, probe-able MPEG-TS. Jellyfin
        // closes each viewer through DirectLiveStream.Close, which releases that viewer's consumer exactly like
        // the plain CloseLiveStream path.
        var live = new DirectLiveStream(
            session.ChannelName,
            session.Path,
            session.StartedUtc,
            uniqueId => BuildOpenedSource(liveStreamId, uniqueId),
            () => CloseLiveStream(liveStreamId, CancellationToken.None),
            _logger,
            session.MarkData);

        _logger.LogInformation(
            "Live Channels: {Name}: handed to Jellyfin as {Path} (session {Session}, live id {Id})",
            session.ChannelName,
            live.MediaSource.Path,
            session.Id,
            liveStreamId);

        return live;
    }

    // Opens a viewer on the channel (adopting the warm session when one exists, else starting a fresh producer)
    // and returns the session with the new viewer's live stream id already attached as a consumer.
    private async Task<(LiveSession Session, string LiveStreamId)> OpenConsumerAsync(string channelId, CancellationToken cancellationToken)
    {
        var channel = _channels.FindChannel(channelId)
            ?? throw new InvalidOperationException("No enabled channel matches id " + channelId);

        _logger.LogInformation("Live Channels: opening live stream for {Name}", channel.Name);

        // A real viewer beats a synthetic benchmark: the stress test deliberately saturates the encoder, which
        // would both stutter this stream and skew the test's own measurement, so tuning in cancels it.
        if (_stress.CancelForViewer())
        {
            _logger.LogInformation("Live Channels: cancelled the running stress test; a viewer tuned in to {Name}", channel.Name);
        }

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
                return (adopted, liveStreamId);
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
                LogFfmpegTail(adopted);
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

                // Notice a producer that dies the moment it happens: while a viewer is still being served, the
                // session starts a replacement encoder rather than going dark until the next reaper sweep.
                WatchProducer(session);

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

        // Jellyfin probes the opened stream immediately, so wait until the segmenter has written the playlist and
        // a couple of segments, and if the producer dies with nothing, drop this consumer (tearing the session
        // down if it was the only one) and surface a clear failure rather than handing over an empty session.
        try
        {
            await WaitForPlaylistAsync(playlist, session.Worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: {Name} produced no playable output", channel.Name);
            if (ex is not OperationCanceledException)
            {
                LogFfmpegTail(session);
            }

            if (ReleaseConsumer(liveStreamId))
            {
                TeardownSession(session);
            }

            throw;
        }

        _activity.Log(
            "Live Channel: " + channel.Name + " has started",
            "LiveChannels.ChannelStarted",
            overview: "This channel is now being encoded.");

        return (session, liveStreamId);
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
        if (session is null || !TeardownSession(session, force: true))
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

        // TeardownSession refuses while the session is still delivering bytes (a viewer whose consumer
        // bookkeeping went wrong is still a viewer). The reaper's over-linger sweep retries every minute, so the
        // session is collected as soon as delivery genuinely stops.
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

    // Marks the stream root as plugin-owned. The marker is what lets the sweeps here, and the configuration
    // validator, tell a directory of ours from one that existed before the plugin: without it a stream root
    // pointed at a pre-existing directory would be indistinguishable from our own.
    private void EnsureStreamRootMarker()
    {
        try
        {
            Directory.CreateDirectory(_streamRoot);
            var marker = Path.Combine(_streamRoot, ChannelService.StreamRootMarkerName);
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, "This directory is managed by the Live Channels plugin. Stream files inside it are deleted automatically.\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not mark the stream root {Path}", _streamRoot);
        }
    }

    // Deletes stream files and concat lists left behind by a previous process that exited without closing its
    // streams. Only safe to call at construction, before this process has opened any of its own streams.
    // Deletion is pattern-restricted: only the plugin's own session directories (lc_<id>) and its known legacy
    // files are ever removed, so a stream root pointed at a directory holding anything else can never have
    // that content swept.
    private void ReapOrphanFiles()
    {
        try
        {
            if (Directory.Exists(_streamRoot))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(_streamRoot))
                {
                    if (IsSessionDir(sessionDir))
                    {
                        TryDeleteDirectory(sessionDir);
                    }
                }

                // The only loose file older versions left in the root: the single-file schedule cache the
                // per-channel cache replaced. Anything else in the root is not ours to delete.
                var legacySchedule = Path.Combine(_streamRoot, "schedule.json");
                if (File.Exists(legacySchedule))
                {
                    TryDeleteFile(legacySchedule);
                }
            }

            foreach (var list in Directory.EnumerateFiles(Path.GetTempPath(), "livechannels-*.txt"))
            {
                TryDeleteFile(list);
            }

            // Older versions kept the generated logos and the icon font in the shared system temp; both now
            // live under Jellyfin's cache, so clear the legacy copies out once.
            TryDeleteDirectory(Path.Combine(Path.GetTempPath(), "livechannels-logos"));
            TryDeleteFile(Path.Combine(Path.GetTempPath(), "livechannels-MaterialSymbolsOutlined.ttf"));

            ReapSubtitleCache();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not reap orphaned temp files");
        }
    }

    // Burn-in subtitles now come from Jellyfin's own subtitle cache (which Jellyfin maintains), so nothing writes
    // here any more. The sweep stays to clear out what earlier versions left behind.
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

        // One snapshot of who is playing for the whole sweep, so every rule below judges the same moment.
        var reported = ReportedStreamIds();

        // Safety net: a viewerless session's delayed teardown normally collects it, but if that task was ever
        // lost, this sweep makes sure a viewerless encoder cannot run forever.
        foreach (var session in DistinctSessions())
        {
            if (session.LingeringSinceUtc is { } since && DateTime.UtcNow - since > LingerGrace * 3
                && !session.HasConsumers && TeardownSession(session, force: false, reported))
            {
                _logger.LogWarning("Live Channels: reaping over-lingered session {Id} ({Name})", session.Id, session.ChannelName);
            }
        }

        var timeoutMinutes = Plugin.Instance?.ReadConfiguration(c => c.SessionTimeoutMinutes) ?? 0;
        if (timeoutMinutes <= 0)
        {
            return;
        }

        // The time limit is a backstop for clients that never send the close, so it only collects streams nobody
        // is watching: TeardownSession refuses while a playback session is playing this stream, and the sweep runs
        // every minute, so a session that reaches the limit mid-watch is collected once its viewer really stops.
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(timeoutMinutes);
        foreach (var session in DistinctSessions())
        {
            if (session.StartedUtc <= cutoff && TeardownSession(session, force: false, reported))
            {
                _logger.LogInformation("Live Channels: closing session {Id} ({Name}) after reaching the {Minutes}-minute time limit", session.Id, session.ChannelName, timeoutMinutes);
            }
        }
    }

    // Reaps sessions whose producer task has already finished (the stream ended or failed) but that Jellyfin
    // never closed, so the CancellationTokenSource and the temp file do not linger for the life of the process.
    // A producer that died while someone was still watching is replaced instead (see TryRestartProducer).
    private void ReapCompletedSessions()
    {
        List<string>? reported = null;
        foreach (var session in DistinctSessions())
        {
            if (!session.Worker.IsCompleted)
            {
                continue;
            }

            if (TryRestartProducer(session))
            {
                continue;
            }

            // Taken lazily: most sweeps find nothing finished and never need to ask who is playing.
            reported ??= ReportedStreamIds();
            if (TeardownSession(session, force: false, reported))
            {
                _logger.LogDebug("Live Channels: reaping finished session {Id} ({Name})", session.Id, session.ChannelName);
            }
        }
    }

    // Watches a session's producer so its death is noticed the moment it happens rather than on the next reaper
    // sweep, which would leave a viewer staring at a stalled picture for up to a minute.
    private void WatchProducer(LiveSession session)
    {
        _ = session.Worker.ContinueWith(_ => TryRestartProducer(session), TaskScheduler.Default);
    }

    // Replaces the producer of a session whose encoder stopped while a viewer is still watching, so an ffmpeg
    // failure costs a few seconds of stall instead of the whole stream. The replacement writes into the SAME
    // session directory: it continues the segment numbering past what is already there (so a reader working
    // through the window is not overwritten) and resumes the channel timeline where the previous output ended (so
    // timestamps move forward, never back). Returns whether a replacement was started.
    private bool TryRestartProducer(LiveSession session)
    {
        // Nothing to rescue when the session is being torn down, when its producer is still running, or when
        // nobody is on the other end: the ordinary reap path collects those. A consumer is enough here (a viewer
        // whose player is mid-buffer is reporting nothing and receiving nothing, and is exactly who this saves).
        if (session.IsTornDown || !session.Worker.IsCompleted || !(session.HasConsumers || IsWatched(session)))
        {
            return false;
        }

        // A session that never produced a single segment did not lose its encoder mid-watch, it failed to start:
        // the tune-in path reports that failure and tears the session down, which is the right answer. Restarting
        // is only for a stream that WAS running.
        var nextSegment = NextSegmentNumber(session.Path);
        if (nextSegment == 0)
        {
            return false;
        }

        var channel = _channels.FindChannel(session.ChannelId);
        if (channel is null || !session.TryClaimRestart(RestartInterval, MaxProducerRestarts))
        {
            return false;
        }

        var timeline = TimeSpan.FromSeconds(session.Stats.TimelineSeconds);
        _logger.LogWarning(
            "Live Channels: the encoder for {Name} stopped while someone was watching; restarting it (attempt {Attempt}) from seg{Segment} at {Timeline:F0}s",
            session.ChannelName,
            session.Restarts,
            nextSegment,
            timeline.TotalSeconds);
        session.Stats.AppendLog("producer stopped unexpectedly; restarting at seg"
            + nextSegment.ToString(CultureInfo.InvariantCulture));

        session.AdoptWorker(Task.Run(
            () => RunProducerAsync(
                () => _streams.StreamToHlsAsync(channel, session.Path, session.Cts.Token, session.Stats, nextSegment, timeline),
                channel.Name),
            CancellationToken.None));
        WatchProducer(session);
        return true;
    }

    /// <summary>
    /// The segment number a replacement segmenter should start writing at: one past the newest segment already in
    /// the session directory, so it appends to the window instead of overwriting segments a reader is still
    /// working through. Zero for a directory with no segments.
    /// </summary>
    /// <param name="sessionDirectory">The session directory holding the rolling <c>seg&lt;n&gt;.ts</c> files.</param>
    /// <returns>The first segment number to write.</returns>
    public static long NextSegmentNumber(string sessionDirectory)
    {
        var highest = -1L;
        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionDirectory, "seg*.ts"))
            {
                var name = Path.GetFileNameWithoutExtension(file.AsSpan());
                if (name.Length > 3 && long.TryParse(name[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > highest)
                {
                    highest = number;
                }
            }
        }
        catch (Exception)
        {
            // The directory is gone or unreadable; start from scratch.
        }

        return highest + 1;
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
        var reported = ReportedStreamIds();
        if (reported is null)
        {
            // The session manager could not be read, so nothing can be said about who is watching; skip the sweep
            // rather than releasing viewers on no evidence.
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

            // Being unreported is circumstantial (some clients simply never report a live stream id); bytes going
            // out of the door are not. A session still delivering keeps every one of its viewers, so a client
            // that reports nothing is never mistaken for one that left.
            if (_live.TryGetValue(id, out var watched) && watched.IsDeliveringData(RecentDeliveryWindow))
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

    // Every live-stream and media-source id Jellyfin's session manager currently reports as playing, or null when
    // the session manager could not be read. This is the authority on who is watching: each entry belongs to a
    // real playback session, and it keeps being reported while a player is paused, which byte delivery cannot say.
    private List<string>? ReportedStreamIds()
    {
        try
        {
            var reported = new List<string>();
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

            return reported;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not snapshot playback sessions");
            return null;
        }
    }

    // Whether anyone is watching a session, against an already-taken snapshot of the reported ids. A playback
    // session reporting one of its viewers is the answer; recent delivery covers the clients that report nothing.
    // When the session manager cannot be read at all, delivery is all there is to go on.
    private static bool IsWatched(LiveSession session, IReadOnlyList<string>? reported)
        => (reported is not null && AnyConsumerReported(session.ConsumerIds, reported))
            || session.IsDeliveringData(RecentDeliveryWindow);

    // Takes its own snapshot, for the callers that check a single session.
    private bool IsWatched(LiveSession session) => IsWatched(session, ReportedStreamIds());

    /// <summary>
    /// Whether any of a session's viewers is reported as playing. The matching rule is the same one the watchdog
    /// uses (see <see cref="SelectUnwatchedConsumers"/>): a client reports a live stream id of the form
    /// <c>{servicePrefix}_{consumerId}</c>, so a consumer is watched when a reported id contains it. Pure and
    /// deterministic so it can be unit tested without a live host.
    /// </summary>
    /// <param name="consumerIds">The session's live consumer ids (one per viewer).</param>
    /// <param name="reportedIds">Every live-stream and media-source id reported by active playback sessions.</param>
    /// <returns>Whether a playback session is playing this stream.</returns>
    public static bool AnyConsumerReported(IEnumerable<string> consumerIds, IEnumerable<string> reportedIds)
    {
        var reported = reportedIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        return consumerIds.Any(consumer => reported.Any(id => id.Contains(consumer, StringComparison.Ordinal)));
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
                    // Only the plugin's own session directories are candidates; the schedule cache and any
                    // foreign directory in a user-configured root are never touched.
                    if (active.Contains(sessionDir) || !IsSessionDir(sessionDir))
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
        var reported = ReportedStreamIds();
        var victims = SelectCapVictims(
            sessions.Select(s => (s.Id, s.StartedUtc, Watched: IsWatched(s, reported))),
            keep.Id,
            cap);

        foreach (var id in victims)
        {
            var victim = sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (victim is not null && TeardownSession(victim, force: false, reported))
            {
                _logger.LogInformation("Live Channels: closing session {Id} ({Name}) to stay within the {Cap}-session cap", victim.Id, victim.ChannelName, cap);
            }
        }

        // Every other session is being watched, so there is nothing the cap may reclaim: run over it rather than
        // cutting someone off. The extra encoder goes away on its own when one of them stops being watched.
        if (sessions.Count > cap && victims.Count < sessions.Count - cap)
        {
            _logger.LogWarning(
                "Live Channels: {Count} sessions are running with a cap of {Cap}, but the rest are being watched; leaving them alone",
                sessions.Count,
                cap);
        }
    }

    /// <summary>
    /// Chooses which sessions to close so the live count fits the cap. Only sessions nobody is watching are
    /// eligible: the cap exists to reclaim streams that leaked, not to cut off a viewer, so when every other
    /// session is being watched it returns fewer victims than the overflow (or none) and the count runs over.
    /// Returns the oldest eligible sessions first and never the one just opened (<paramref name="keep"/>). A cap
    /// of zero or less is unlimited and selects nothing. Pure and deterministic so it can be unit tested without a
    /// live host.
    /// </summary>
    /// <param name="sessions">Every live session as an id, its start time, and whether it is currently delivering to a viewer.</param>
    /// <param name="keep">The just-opened session that must never be evicted.</param>
    /// <param name="cap">The maximum number of concurrent sessions; zero or less means unlimited.</param>
    /// <returns>The ids to close, oldest first.</returns>
    public static List<string> SelectCapVictims(IEnumerable<(string Id, DateTime StartedUtc, bool Watched)> sessions, string keep, int cap)
    {
        var all = sessions.ToList();
        var overflow = all.Count - cap;
        if (cap <= 0 || overflow <= 0)
        {
            return new List<string>();
        }

        return all
            .Where(s => !s.Watched && !string.Equals(s.Id, keep, StringComparison.Ordinal))
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
    //
    // A session someone is watching is never collected by a background rule (the linger, the reaper, the
    // concurrency cap, or the time limit): those rules exist to reclaim streams nobody is watching, and each one
    // has at some point been able to cut off a viewer who was. Only an explicit administrator kill and server
    // shutdown pass force, because those are deliberate.
    private bool TeardownSession(LiveSession session, bool force = false)
        => TeardownSession(session, force, force ? null : ReportedStreamIds());

    // The overload the sweeps use, so one pass over every session shares a single snapshot of who is playing.
    private bool TeardownSession(LiveSession session, bool force, IReadOnlyList<string>? reported)
    {
        if (!force && IsWatched(session, reported))
        {
            _logger.LogDebug(
                "Live Channels: keeping session {Id} ({Name}) alive; someone is watching it",
                session.Id,
                session.ChannelName);
            return false;
        }

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

    /// <summary>
    /// Whether a directory under the stream root is one of the plugin's own session directories: the
    /// <c>lc_</c>-prefixed 32-hex-digit shape <see cref="NewLiveId"/> produces. The orphan and cleanup sweeps
    /// delete ONLY these, so the schedule cache and anything foreign in a user-configured stream root can
    /// never be swept.
    /// </summary>
    /// <param name="path">The directory path to judge.</param>
    /// <returns>Whether the directory name is a plugin session id.</returns>
    internal static bool IsSessionDir(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length != 35 || !name.StartsWith("lc_", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 3; i < name.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(name[i]))
            {
                return false;
            }
        }

        return true;
    }

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

    // DVR timer surface, backed by TimerService and fulfilled by RecordingService: when a timer's window ends,
    // the aired program's library file is materialized into the Live TV recordings folder (see
    // RecordingService for why a recording must be a real file there). Creates route through
    // ISupportsNewTimerIds so Jellyfin learns the id each new timer was stored under.

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(_timers.GetTimers());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(_timers.GetSeriesTimers());

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(TimerService.NewTimerDefaults(program));

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        _timers.SaveTimer(info);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        _timers.SaveSeriesTimer(info);

        // Expand the rule into its child timers right away (not just on the next heartbeat), so the client
        // that created it sees the scheduled airings on its very next timers fetch.
        _recordings.Kick();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    Task<string> ISupportsNewTimerIds.CreateTimer(TimerInfo info, CancellationToken cancellationToken)
        => Task.FromResult(_timers.SaveTimer(info));

    /// <inheritdoc />
    Task<string> ISupportsNewTimerIds.CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        var id = _timers.SaveSeriesTimer(info);
        _recordings.Kick();
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        _timers.SaveTimer(updatedTimer);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        _timers.SaveSeriesTimer(info);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        _timers.CancelTimer(timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        _timers.CancelSeriesTimer(timerId);
        return Task.CompletedTask;
    }

    // Builds the "menu" source descriptor Jellyfin lists before a stream is opened. We produce the stream
    // ourselves at a known resolution/codec, so it advertises concrete video/audio streams: it cannot be probed
    // (nothing is running yet), and without declared streams a client that needs transcoding crashes Jellyfin's
    // scale filter on the null source dimensions.
    private MediaSourceInfo BuildMenuSource(string id)
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

        // The source id feeds Jellyfin's probe cache key (via the open token), so fold the output shape into it:
        // any config change that alters the stream (resolution, bitrate, codecs) mints a new id, forcing a fresh
        // probe instead of serving stale cached metadata. The revision tag also retires every cache entry written
        // by the pre-endpoint architecture; those carry Container "hls", which makes delivery run `-f hls`
        // against the raw TS endpoint and fail on the spot.
        var fingerprint = Hash(string.Create(
            CultureInfo.InvariantCulture,
            $"v2|{width}|{bitrateKbps}|{(int)videoCodec}|{(int)audioCodec}"));

        return new MediaSourceInfo
        {
            Id = id + "_" + fingerprint,
            Protocol = MediaProtocol.File,
            Container = "mpegts",
            IsInfiniteStream = true,
            BufferMs = StreamSessionService.BufferSeconds() * 1000,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsProbing = false,
            MediaStreams = new[] { video, audio }
        };
    }

    // Builds the OPENED source descriptor. Its path is Jellyfin's OWN internal live stream endpoint (the same
    // one every native tuner stream flows through), which serves the session's segments as one continuous
    // MPEG-TS (see DirectLiveStream). It declares no streams and asks to be probed: after the open returns,
    // Jellyfin's live-TV provider force-flags every plugin-provided video stream as interlaced (a legacy "make
    // clients deinterlace" hack that made clients re-encode with "interlaced video is not supported"), and the
    // open-stream probe runs AFTER that hack, replacing the doctored streams with what the output really is
    // (progressive), so playback direct-streams. The handover already waited for the first segments, so the
    // probe has real content to read; probing a continuous TS is also immune to the 10.11.10+ probe container
    // normalisation that broke probing the HLS playlist directly (it rewrote the container to "ts" and delivery
    // then ran `-f mpegts` against a .m3u8 -- fatal on every tune-in).
    private MediaSourceInfo BuildOpenedSource(string liveStreamId, string uniqueId)
    {
        return new MediaSourceInfo
        {
            Id = liveStreamId,
            Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + uniqueId + "/stream.ts",
            Protocol = MediaProtocol.Http,
            IsInfiniteStream = true,
            // Deliberately NOT ReadAtNativeFramerate: the endpoint already paces the reader to segment
            // availability at the live edge. Adding -re on top caps it at 1x realtime even when it is behind, so
            // the client buffer could only ever fill at realtime (slow tune-in) and a reader that hiccuped could
            // never catch back up before falling off the back of the delete window.
            // Pre-roll the configured start-up buffer on the client so playback starts on a full buffer instead
            // of stuttering while it fills (the declarative form of pausing briefly then resuming on tune-in).
            // The handover already waited for that much content to exist, so this fills at I/O speed off the disk
            // rather than at realtime.
            BufferMs = StreamSessionService.BufferSeconds() * 1000,
            RequiresOpening = false,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsProbing = true,
            MediaStreams = Array.Empty<MediaStream>()
        };
    }

    private static async Task WaitForPlaylistAsync(string playlist, Task worker, CancellationToken cancellationToken)
    {
        // Hand Jellyfin the playlist only once the segmenter has written it and buffered the configured start-up
        // cushion, so playback starts on content that already exists instead of chasing the encoder (the per-item
        // path spawns a fresh ffmpeg per item, and these buffered segments ride over the gap between items). The
        // producer bursts well ahead of realtime, so filling the cushion costs a fraction of its length in real
        // time, and it keeps filling past this gate while Jellyfin spins up its own repackager.
        var minSegments = StreamArguments.SegmentsForBuffer(StreamSessionService.BufferSeconds());
        var dir = Path.GetDirectoryName(playlist) ?? string.Empty;

        // The deadline scales with the cushion: a deep buffer legitimately takes longer to fill, and a tune-in
        // must never be failed for waiting exactly as long as it was told to.
        var deadline = DateTime.UtcNow.AddSeconds(20 + (minSegments * StreamArguments.SegmentSeconds));
        while (DateTime.UtcNow < deadline && !worker.IsCompleted)
        {
            if (File.Exists(playlist) && CountSegments(dir) >= minSegments)
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

    // Surfaces the tail of a failed session's ffmpeg diagnostic log in the server log, at warning so it shows at
    // the default level. The log lives in the session directory, which teardown deletes, and per-process stderr
    // is otherwise only logged at debug -- so this moment is the only chance to attach the CAUSE (the spawned
    // commands, exit codes, and stderr) to a "produced no playable output" report instead of just the symptom.
    private void LogFfmpegTail(LiveSession session)
    {
        const int MaxTailChars = 4000;
        try
        {
            var path = session.Stats.LogPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (text.Length > MaxTailChars)
            {
                text = "[… older log trimmed …]" + text[^MaxTailChars..];
            }

            _logger.LogWarning("Live Channels: ffmpeg log for the failed {Name} session:\n{FfmpegLog}", session.ChannelName, text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: could not read the session ffmpeg log for {Name}", session.ChannelName);
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
        private long _lastDataTicks;
        private DateTime _lastRestartUtc = DateTime.MinValue;

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

        // The producer task. Replaced (not restarted in place) when a producer dies under a viewer who is still
        // watching, so the session survives an encoder failure instead of taking the viewer down with it.
        public Task Worker { get; private set; }

        // How many replacement producers this session has run, so a permanently broken pipeline stops being
        // restarted rather than looping forever.
        public int Restarts { get; private set; }

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

        // Whether the session has been torn down, so a producer that finishes because of that teardown is not
        // mistaken for one that died on its own.
        public bool IsTornDown => Volatile.Read(ref _tornDown) != 0;

        // The live stream ids of this session's viewers, which is what a playing client reports back to Jellyfin.
        public IReadOnlyList<string> ConsumerIds
        {
            get
            {
                lock (_gate)
                {
                    return _consumers.ToList();
                }
            }
        }

        /// <summary>Records that a reader was served bytes: someone is pulling this stream right now.</summary>
        public void MarkData() => Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);

        /// <summary>Whether the session delivered data to a reader within the given window.</summary>
        /// <param name="window">How recent the delivery must be.</param>
        /// <returns>Whether bytes went out recently.</returns>
        public bool IsDeliveringData(TimeSpan window)
        {
            var ticks = Interlocked.Read(ref _lastDataTicks);
            return ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < window;
        }

        // Claims the right to start a replacement producer, refusing when the session is gone, when replacements
        // are coming too fast, or when it has already had too many.
        public bool TryClaimRestart(TimeSpan minimumInterval, int maxRestarts)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (IsTornDown || Restarts >= maxRestarts || now - _lastRestartUtc < minimumInterval)
                {
                    return false;
                }

                _lastRestartUtc = now;
                Restarts++;
                return true;
            }
        }

        /// <summary>Attaches the replacement producer this session now runs on.</summary>
        /// <param name="worker">The new producer task.</param>
        public void AdoptWorker(Task worker)
        {
            lock (_gate)
            {
                Worker = worker;
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
