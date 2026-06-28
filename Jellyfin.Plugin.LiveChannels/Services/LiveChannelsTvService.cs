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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
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

    // Default client pre-roll when the configured BufferSeconds is unreadable. A live channel is a re-encoded
    // feed the player joins mid-stream, so without a head start it begins on an almost-empty buffer and stutters
    // until it fills. Pre-rolling a few seconds (the declarative form of "pause briefly, then resume") starts
    // playback on a full cushion instead; the amount is configurable in Settings.
    private const int DefaultBufferSeconds = 3;

    private readonly ChannelService _channels;
    private readonly StreamSessionService _streams;
    private readonly DefaultLogoService _defaultLogo;
    private readonly ActivityLogger _activity;
    private readonly ILogger<LiveChannelsTvService> _logger;

    private readonly string _streamRoot;
    private readonly string _logoRoot = Path.Combine(Path.GetTempPath(), "livechannels-logos");
    private readonly ConcurrentDictionary<string, LiveSession> _live = new(StringComparer.Ordinal);
    private readonly Timer _reaper;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsTvService"/> class.
    /// </summary>
    /// <param name="channels">The channel service, used to resolve channels and their schedule.</param>
    /// <param name="streams">The stream session service, used to produce each channel's ffmpeg feed.</param>
    /// <param name="defaultLogo">The generated fallback-logo service.</param>
    /// <param name="activity">The activity logger, used to record channel start/stop in Jellyfin's activity log.</param>
    /// <param name="appPaths">The application paths, used to default the stream directory under Jellyfin's cache.</param>
    /// <param name="logger">The logger.</param>
    public LiveChannelsTvService(ChannelService channels, StreamSessionService streams, DefaultLogoService defaultLogo, ActivityLogger activity, IApplicationPaths appPaths, ILogger<LiveChannelsTvService> logger)
    {
        _channels = channels;
        _streams = streams;
        _defaultLogo = defaultLogo;
        _activity = activity;
        _logger = logger;

        // Where each channel's growing stream file is written. Configurable, since the file lives for the whole
        // watch and the system temp is often small or RAM-backed; defaults to a livechannels folder in Jellyfin's
        // cache. A change takes effect on the next restart (active streams keep using the directory they started in).
        var configured = Plugin.Instance?.ReadConfiguration(c => c.StreamDirectory);
        _streamRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(appPaths.CachePath, "livechannels")
            : configured;

        // A fresh process owns no live streams, so anything left in the stream root is an orphan from a previous
        // run that ended without CloseLiveStream (a crash). Sweep it now, then periodically reap sessions whose
        // producer has stopped but that Jellyfin never closed, so neither files nor encoders pile up.
        ReapOrphanFiles();
        _reaper = new Timer(_ => ReapCompletedSessions(), state: null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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

        var programs = _channels.ResolvePrograms(channel);
        if (programs.Count == 0)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var schedule = ScheduleCalculator.BuildSchedule(programs, startDateUtc, endDateUtc, ScheduleCalculator.Epoch);
        var newSince = DateTime.UtcNow.AddDays(-NewWindowDays);

        var list = new List<ProgramInfo>(schedule.Count);
        foreach (var slot in schedule)
        {
            var p = slot.Program;
            list.Add(new ProgramInfo
            {
                Id = channelId + "_" + slot.Start.Ticks.ToString(CultureInfo.InvariantCulture),
                ChannelId = channelId,
                Name = p.Title,
                Overview = p.Overview,
                StartDate = slot.Start,
                EndDate = slot.Stop,
                Genres = p.Genres.ToList(),
                OfficialRating = p.OfficialRating,
                ProductionYear = p.Year,
                SeasonNumber = p.SeasonNumber,
                EpisodeNumber = p.EpisodeNumber,
                IsSeries = p.SeriesId.HasValue,
                IsMovie = p.IsMovie,
                IsKids = p.IsKids,
                IsRepeat = p.DateAdded < newSince,
                ImagePath = p.PrimaryImagePath,
                HasImage = !string.IsNullOrEmpty(p.PrimaryImagePath)
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
        var liveStreamId = "lc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        // Each session gets its own directory holding the live playlist and its rolling segments; the whole
        // directory is removed on close. The session tracks the directory (for cleanup); Jellyfin is handed the
        // playlist inside it.
        var dir = Path.Combine(_streamRoot, liveStreamId);
        Directory.CreateDirectory(dir);
        var playlist = Path.Combine(dir, "stream.m3u8");

        var cts = new CancellationTokenSource();

        var worker = StartProducer(channel, dir, cts);

        _live[liveStreamId] = new LiveSession(cts, worker, dir, channel.Name, channelId);

        // Jellyfin shares one live stream per channel, so any earlier session still open for this channel was
        // abandoned (a re-tune Jellyfin never closed). Tear those down now so producers and their downstream
        // transcodes do not pile up and saturate the box. Also sweep any finished sessions while we are here.
        EvictStaleSessions(channelId, keep: liveStreamId);

        // Jellyfin opens the playlist immediately, so wait until the segmenter has written it and a first segment,
        // and if the producer dies with nothing, tear the session down and surface a clear failure rather than
        // handing over an empty playlist.
        try
        {
            await WaitForPlaylistAsync(playlist, worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_live.TryRemove(liveStreamId, out var failed))
            {
                try
                {
                    await failed.Cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Already finished.
                }

                DisposeSession(failed);
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
    private Task StartProducer(Channel channel, string dir, CancellationTokenSource cts)
    {
        return Task.Run(() => RunProducerAsync(() => _streams.StreamToHlsAsync(channel, dir, cts.Token), channel.Name), CancellationToken.None);
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
    public async Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        if (!_live.TryRemove(id, out var session))
        {
            return;
        }

        _activity.Log(
            "Live Channel: " + session.ChannelName + " has stopped",
            "LiveChannels.ChannelStopped",
            overview: "This channel has no viewers so encoding has been stopped.");

        try
        {
            await session.Cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        try
        {
            await session.Worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live stream {Id} did not stop promptly", id);
        }

        DisposeSession(session);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _reaper.Dispose();

        // Stop and clean up every live stream still open at shutdown.
        foreach (var id in _live.Keys.ToList())
        {
            if (_live.TryRemove(id, out var session))
            {
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
                    TryDeleteDirectory(sessionDir);
                }

                // Tidy any stray loose files from older (single-file) versions.
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

    // Reaps sessions whose producer task has already finished (the stream ended or failed) but that Jellyfin
    // never closed, so the CancellationTokenSource and the temp file do not linger for the life of the process.
    private void ReapCompletedSessions()
    {
        foreach (var (id, session) in _live)
        {
            if (!session.Worker.IsCompleted)
            {
                continue;
            }

            if (_live.TryRemove(id, out var removed))
            {
                _logger.LogDebug("Live Channels: reaping finished session {Id} ({Name})", id, removed.ChannelName);
                DisposeSession(removed);
            }
        }
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

        var active = new HashSet<string>(_live.Values.Select(s => s.Path), StringComparer.Ordinal);
        var removed = 0;
        try
        {
            if (Directory.Exists(_streamRoot))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(_streamRoot))
                {
                    if (active.Contains(sessionDir))
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

    // Tears down every session except the one just opened: any other session for the same channel is an abandoned
    // re-tune Jellyfin never closed, and any finished session anywhere is dead weight. Each producer's CTS is
    // cancelled (which ends its ffmpeg) without blocking the caller, since this runs on the tune-in path.
    private void EvictStaleSessions(string channelId, string keep)
    {
        foreach (var (id, session) in _live)
        {
            if (string.Equals(id, keep, StringComparison.Ordinal))
            {
                continue;
            }

            var sameChannel = string.Equals(session.ChannelId, channelId, StringComparison.Ordinal);
            if (!sameChannel && !session.Worker.IsCompleted)
            {
                continue;
            }

            if (_live.TryRemove(id, out var removed))
            {
                _logger.LogInformation("Live Channels: evicting stale session {Id} ({Name})", id, removed.ChannelName);
                try
                {
                    removed.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already finished.
                }

                // Delete the session directory only once the producer has fully stopped (its segmenter is killed in
                // StreamToHlsAsync's finally), so the recursive delete cannot race the segmenter still writing
                // segments into it. Fire-and-forget so an incoming re-tune is never blocked draining the old one.
                _ = removed.Worker.ContinueWith(_ => DisposeSession(removed), TaskScheduler.Default);
            }
        }
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
    }

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
    private static MediaSourceInfo BuildSource(string id, string? path)
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

        return new MediaSourceInfo
        {
            Id = id,
            Path = path,
            Protocol = MediaProtocol.File,
            Container = "hls",
            IsInfiniteStream = true,
            // Read the stream at native frame rate so Jellyfin's transcoder paces itself to realtime. The live
            // playlist (no end tag) already paces it to segment availability; this keeps the transcoder honest.
            ReadAtNativeFramerate = true,
            // Pre-roll the configured number of seconds on the client so playback starts on a full buffer instead
            // of stuttering while it fills (the declarative form of pausing briefly then resuming on tune-in).
            BufferMs = Math.Max(0, Plugin.Instance?.ReadConfiguration(c => c.BufferSeconds) ?? DefaultBufferSeconds) * 1000,
            RequiresOpening = path is null,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsProbing = false,
            MediaStreams = new[] { video, audio }
        };
    }

    private static async Task WaitForPlaylistAsync(string playlist, Task worker, CancellationToken cancellationToken)
    {
        // Hand Jellyfin the playlist only once the segmenter has written it and buffered a few segments, so
        // playback starts on a small cushion (the per-item path spawns a fresh ffmpeg per item, and these
        // buffered segments ride over the brief gap between items). The initial burst fills them fast.
        const int MinSegments = 3;
        var dir = Path.GetDirectoryName(playlist) ?? string.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !worker.IsCompleted)
        {
            if (File.Exists(playlist) && CountSegments(dir) >= MinSegments)
            {
                return;
            }

            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // The loop ended either because the segmenter is alive but still filling (deadline hit — hand over the
        // playlist, it has segments and will keep growing) or because the producer finished. If it finished
        // without producing a playable playlist, it produced nothing: fail loudly so the caller tears it down.
        if (worker.IsCompleted && !(File.Exists(playlist) && CountSegments(dir) >= 1))
        {
            throw new InvalidOperationException("The channel produced no playable output.");
        }
    }

    // Counts the current HLS segments in a session directory (the rolling window the segmenter keeps on disk).
    private static int CountSegments(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ts").Length : 0;
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

    private sealed record LiveSession(CancellationTokenSource Cts, Task Worker, string Path, string ChannelName, string ChannelId);
}
