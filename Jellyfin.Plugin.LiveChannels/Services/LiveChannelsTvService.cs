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
    private const int BufferSize = 81920;

    // Content added within this window is treated as new (not a repeat) in the guide.
    private const int NewWindowDays = 14;

    private readonly ChannelService _channels;
    private readonly StreamSessionService _streams;
    private readonly DefaultLogoService _defaultLogo;
    private readonly ActivityLogger _activity;
    private readonly ILogger<LiveChannelsTvService> _logger;

    private readonly string _streamRoot = Path.Combine(Path.GetTempPath(), "livechannels-streams");
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
    /// <param name="logger">The logger.</param>
    public LiveChannelsTvService(ChannelService channels, StreamSessionService streams, DefaultLogoService defaultLogo, ActivityLogger activity, ILogger<LiveChannelsTvService> logger)
    {
        _channels = channels;
        _streams = streams;
        _defaultLogo = defaultLogo;
        _activity = activity;
        _logger = logger;

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
        Directory.CreateDirectory(_streamRoot);
        var liveStreamId = "lc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var path = Path.Combine(_streamRoot, liveStreamId + ".ts");

        var cts = new CancellationTokenSource();

        var worker = StartFileProducer(channel, path, cts);

        _live[liveStreamId] = new LiveSession(cts, worker, path, channel.Name, channelId);

        // Jellyfin shares one live stream per channel, so any earlier session still open for this channel was
        // abandoned (a re-tune Jellyfin never closed). Tear those down now so producers and their downstream
        // transcodes do not pile up and saturate the box. Also sweep any finished sessions while we are here.
        EvictStaleSessions(channelId, keep: liveStreamId);

        // Jellyfin opens the temp file immediately, so wait until the producer has buffered enough to play and,
        // if it dies with nothing, tear the session down and surface a clear failure rather than handing over an
        // empty file.
        try
        {
            await WaitForBytesAsync(path, worker, cancellationToken).ConfigureAwait(false);
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

        return BuildSource(liveStreamId, path);
    }

    // Starts the file-backed producer: opens a temp file the producer appends to and Jellyfin reads, disposing it
    // when the producer stops (it is deleted on close).
    private Task StartFileProducer(Channel channel, string path, CancellationTokenSource cts)
    {
        var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, BufferSize, useAsync: true);
        return Task.Run(() => RunProducerAsync(() => _streams.StreamToAsync(channel, output, cts.Token), channel.Name, output), CancellationToken.None);
    }

    // Runs a producer delegate with the standard lifecycle: swallow cancellation, log any real failure, and
    // dispose the output file.
    private async Task RunProducerAsync(Func<Task> produce, string channelName, FileStream? output)
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
        finally
        {
            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not dispose stream file for {Name}", channelName);
                }
            }
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

    // Tears down every session except the one just opened: any other session for the same channel is an abandoned
    // re-tune Jellyfin never closed, and any finished session anywhere is dead weight. Each producer's CTS is
    // cancelled (which ends its ffmpeg and lets the file be deleted) without blocking the caller.
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

                DisposeSession(removed);
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

        TryDeleteFile(session.Path);
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

    // Builds the live MPEG-TS source descriptor. With no path it is the "menu" entry Jellyfin opens via
    // GetChannelStream; with a path it is the opened stream Jellyfin reads and later closes by its Id.
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
            Container = "ts",
            IsInfiniteStream = true,
            // Read the stream at native frame rate so Jellyfin's transcoder paces itself to realtime instead of
            // racing through our growing file to EOF and ending the stream after a few seconds (a regular file
            // returns EOF the moment the reader catches the producer's write head).
            ReadAtNativeFramerate = true,
            RequiresOpening = path is null,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsProbing = false,
            MediaStreams = new[] { video, audio }
        };
    }

    private static async Task WaitForBytesAsync(string path, Task worker, CancellationToken cancellationToken)
    {
        // Hand Jellyfin the path only once the producer is a few seconds ahead: enough for ffmpeg to find
        // codec parameters, and a head-start buffer so the brief gap between items (the per-item path spawns
        // a fresh ffmpeg per item) is absorbed by the buffered temp file and never reaches the player.
        var bitrateKbps = Plugin.Instance?.ReadConfiguration(c => c.TranscodeVideoBitrateKbps) ?? 4000;
        var minReadyBytes = Math.Max(2_000_000L, (long)bitrateKbps * 1000L / 8L * 4L);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !worker.IsCompleted)
        {
            try
            {
                if (new FileInfo(path).Length >= minReadyBytes)
                {
                    return;
                }
            }
            catch (IOException)
            {
                // The file may not exist yet; keep waiting.
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

        // The loop ended either because the producer is alive but still buffering (deadline hit — hand over the
        // partial file, it has data) or because the producer finished. If it finished without buffering the
        // minimum, it produced nothing playable: fail loudly so the caller can tear the dead session down.
        if (worker.IsCompleted)
        {
            long length = 0;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // Treat an unreadable/absent file as empty.
            }

            if (length < minReadyBytes)
            {
                throw new InvalidOperationException("The channel produced no playable output.");
            }
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
