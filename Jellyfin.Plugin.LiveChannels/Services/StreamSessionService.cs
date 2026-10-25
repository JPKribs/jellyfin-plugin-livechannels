using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Streams a channel as a continuous MPEG-TS feed. It computes which item is airing now from the same
/// wall-clock schedule the guide uses, seeks into it, and then plays each subsequent item end to end via
/// ffmpeg, looping until the client disconnects.
/// </summary>
public class StreamSessionService
{
    private const int BufferSize = 81920;

    // How many consecutive per-item failures (missing files, instant encoder deaths) before the channel gives up
    // and shows standby. Failures reset on any success, so this only trips when the library is genuinely
    // unreachable — and then quickly (~5s), instead of walking every item of a large playlist first.
    private const int MaxConsecutiveFailures = 25;

    private readonly IMediaEncoder _encoder;
    private readonly ChannelService _channels;
    private readonly EncoderResolver _encoders;
    private readonly IntroService _intros;
    private readonly ILogger<StreamSessionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSessionService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="channels">The channel service, used to resolve and schedule the channel's items.</param>
    /// <param name="encoders">The encoder resolver, used to pick software/hardware encoders.</param>
    /// <param name="intros">The tune-in card cache, played ahead of the first item of a fresh session.</param>
    /// <param name="logger">The logger.</param>
    public StreamSessionService(IMediaEncoder encoder, ChannelService channels, EncoderResolver encoders, IntroService intros, ILogger<StreamSessionService> logger)
    {
        _encoder = encoder;
        _channels = channels;
        _encoders = encoders;
        _intros = intros;
        _logger = logger;
    }

    /// <summary>
    /// Streams the channel to <paramref name="output"/> until cancellation. Each call is independent so the
    /// in-process Live TV service can run a separate encode per viewer; cancelling stops the encode.
    /// </summary>
    /// <param name="channel">The channel to stream.</param>
    /// <param name="output">The destination stream (the temp file Jellyfin reads).</param>
    /// <param name="cancellationToken">Cancelled when the live stream is closed.</param>
    /// <param name="stats">The shared per-session stats sink, or <c>null</c>.</param>
    /// <param name="timelineBase">Where on the channel timeline this producer's output starts. Non-zero only when a producer is restarted under a viewer who is still watching, so the timestamps it appends continue forward instead of rewinding to zero.</param>
    /// <returns>A task that completes when streaming stops.</returns>
    public async Task StreamToAsync(Channel channel, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan timelineBase = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(output);

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("No ffmpeg encoder is configured; cannot stream channel {Name}", channel.Name);
            return;
        }

        // A fresh session opens on the tune-in card: its bytes go into the segmenter right now, before the
        // schedule is even resolved, so the first segment closes (and the tune-in completes) within a fraction of
        // a second while the real content is seeked and encoded behind it. The first producer then chains its
        // timeline from the card's true end exactly as every item chains from the previous one, and the
        // schedule position it seeks to is advanced by the card's length so the content still lands on the
        // wall clock the guide shows. A restarted producer (non-zero timeline) is continuing under a viewer and
        // never replays the card.
        var lead = TimeSpan.Zero;
        var card = timelineBase == TimeSpan.Zero ? _intros.TryGetCached(channel) : null;
        if (timelineBase == TimeSpan.Zero && card is null)
        {
            _logger.LogInformation("Live Channels: {Name}: no tune-in card is rendered for the current output settings yet; opening straight on content", channel.Name);
        }

        if (card is { } intro)
        {
            try
            {
                var file = new FileStream(intro.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    await file.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                timelineBase = NextTimeline(TimeSpan.Zero, intro.Duration, intro.EndSeconds);
                lead = intro.Duration;
                _logger.LogInformation("Live Channels: {Name}: opened on the tune-in card ({Seconds:F1}s, content chains at {Timeline:F3}s)", channel.Name, intro.Duration.TotalSeconds, timelineBase.TotalSeconds);
                stats?.AppendLog("Tune-in card: " + intro.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s from " + intro.Path + ", content chains at " + timelineBase.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + "s");
            }
            catch (IOException ex)
            {
                // The card could not be delivered (an unreadable cache file, or the segmenter has already gone):
                // carry on without it; a dead segmenter fails the producer the same way a moment later.
                _logger.LogWarning(ex, "Live Channels: {Name}: could not play the tune-in card; starting on content", channel.Name);
            }
        }

        // A restarted producer continues under a viewer, and the producer it replaces had run ahead of the wall
        // clock by up to its head start. Resume the schedule where that output actually ended (the session's
        // anchor plus the timeline it reached) rather than at plain "now", which would replay that lead.
        var restarted = timelineBase > TimeSpan.Zero && card is null;
        if (restarted && stats?.ContentAnchorUtc is { } sessionAnchor)
        {
            var ahead = sessionAnchor + timelineBase - DateTime.UtcNow;
            if (ahead > TimeSpan.Zero && ahead <= TimeSpan.FromSeconds(2 * StreamArguments.InitialBurstSeconds))
            {
                lead = ahead;
            }
        }

        // The guide time timeline zero airs at, shared through the stats so a later restart can find its place.
        var startUtc = DateTime.UtcNow + lead;
        var anchor = stats?.SetContentAnchorOnce(startUtc - timelineBase) ?? startUtc - timelineBase;

        var programs = _channels.ResolvePrograms(channel);
        if (programs.Count == 0)
        {
            _logger.LogWarning("Live channel {Name} resolved to no playable items; showing standby", channel.Name);
            await StreamSlateAsync(ffmpeg, timelineBase, output, cancellationToken, stats).ConfigureAwait(false);
            return;
        }

        // Follows the loop if it is rebuilt while this session plays (the daily guide refresh, or a configuration
        // save), so a viewer who stays tuned keeps seeing what the guide shows. The channel is looked up afresh so a
        // rebuild after a save uses the saved configuration, never the copy this session started with.
        var channelId = channel.Id;
        var follower = new ProgramsFollower(channel, programs, () =>
            _channels.FindChannel(channelId) is { } current ? (current, _channels.ResolvePrograms(current)) : null);

        // A channel whose rating limits vary by time of day is scheduled per daypart, not as a free-running loop,
        // so it always takes the per-item path: the concat path plays one fixed playlist order and cannot reorder
        // content by the clock. Ordinary channels are unaffected.
        var timeOfDay = _channels.IsTimeOfDayChannel(channel);

        // The seamless concat pipeline must software-decode (hardware decoders fail on the per-item resolution
        // changes a playlist produces), and software decoding can't keep up with >1080p sources (e.g. 4K HEVC).
        // So a channel containing any high-resolution item, or one using subtitle burn-in (which needs a
        // per-item filter graph), streams item by item; that path hardware-decodes each item at its own size.
        // HDR also forces the per-item path: only it tone-maps each item to SDR, so a channel with any HDR item
        // (even <=1080p) plays with correct colour instead of washed-out grey.
        // A GPU-upload encoder (QSV/VAAPI, whose pixel stage ends in hwupload) cannot survive the filter-graph
        // reinit the concat demuxer triggers when the playlist crosses a resolution/format change: the hwupload
        // fails to re-init ("Impossible to convert between formats", exit 218). The per-item path runs one ffmpeg
        // per item, so there is no mid-stream reinit. Force those encoders per-item.
        var videoCodec = Plugin.Instance?.ReadConfiguration(c => c.VideoCodec) ?? Models.VideoCodec.H264;
        var usesHwUpload = _encoders.ResolveVideo(videoCodec, allowHardware: true).PixelStage.Contains("hwupload", StringComparison.Ordinal);
        var highRes = programs.Any(p => p.SourceHeight > 1080);

        // Whether any item is HDR (which forces the per-item tone-map path). Each item's HDR flag is probed once at
        // guide refresh and cached on the schedule entry, so this is a pure in-memory scan with no media-stream
        // queries on the tune-in critical path. The short-circuit skips even the scan when the channel is
        // already per-item for another reason (burn-in, a >1080p item, or a GPU-upload encoder).
        var alreadyPerItem = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never || highRes || usesHwUpload;
        var hasHdr = !alreadyPerItem && programs.Any(p => p.IsHdr);
        var perItem = alreadyPerItem || hasHdr || timeOfDay;
        var uniform = IsUniform(programs);

        // A fresh session bursts the full head start; a restart only tops up whatever lead it has lost, so the
        // live edge returns to schedule without lurching past it.
        var headCredit = restarted ? lead : TimeSpan.Zero;

        if (!perItem)
        {
            var (concatIndex, _) = ScheduleCalculator.CurrentProgram(programs, startUtc, ScheduleCalculator.AnchorFor(programs));
            LogEncodePlan(channel, programs.Count, concatIndex, perItem, uniform, hasHdr);
            await StreamConcatAsync(ffmpeg, channel, follower, output, cancellationToken, stats, timelineBase, anchor, headCredit).ConfigureAwait(false);
            return;
        }

        IStreamSchedule schedule;
        int planIndex;
        if (timeOfDay)
        {
            schedule = new DaypartStreamSchedule(_channels, channel, programs, startUtc, follower);
            planIndex = 0;
        }
        else
        {
            var (index, offset) = ScheduleCalculator.CurrentProgram(programs, startUtc, ScheduleCalculator.AnchorFor(programs));
            schedule = new LoopStreamSchedule(programs, index, offset, startUtc, follower);
            planIndex = index;
        }

        LogEncodePlan(channel, programs.Count, planIndex, perItem, uniform, hasHdr);
        await StreamPerItemLoopAsync(ffmpeg, channel, schedule, output, cancellationToken, stats, timelineBase, headCredit).ConfigureAwait(false);
    }

    // Whether every item shares one known source height, which lets the continuous pipeline hardware-decode (the
    // concat demuxer feeds one shared decoder, and a hardware decoder cannot follow a mid-stream resolution change).
    private static bool IsUniform(IReadOnlyList<ProgramEntry> programs)
        => programs.Count > 0 && programs[0].SourceHeight > 0 && programs.All(p => p.SourceHeight == programs[0].SourceHeight);

    /// <summary>
    /// Streams the channel as a self-trimming HLS playlist in <paramref name="hlsDir"/>. A long-lived segmenter
    /// ffmpeg copies the producer's continuous MPEG-TS into a rolling window of small segments plus a live
    /// playlist, so on-disk size stays bounded for any watch length. The producer (per-item or concat, plus the
    /// standby slate) feeds the segmenter's stdin through the existing <see cref="StreamToAsync"/> path.
    /// </summary>
    /// <param name="channel">The channel to stream.</param>
    /// <param name="hlsDir">The directory the playlist and segments are written to.</param>
    /// <param name="cancellationToken">Cancelled when the live stream is closed.</param>
    /// <param name="stats">The shared per-session stats sink, or <c>null</c>.</param>
    /// <param name="startSegmentNumber">The first segment number to write. Non-zero when this producer is restarted into a directory that already holds segments a reader is still working through, so the new segmenter appends past them instead of overwriting them.</param>
    /// <param name="timelineBase">Where on the channel timeline this producer's output starts, so a restart continues the timestamps forward.</param>
    /// <returns>A task that completes when streaming stops.</returns>
    public async Task StreamToHlsAsync(Channel channel, string hlsDir, CancellationToken cancellationToken, SessionStats? stats = null, long startSegmentNumber = 0, TimeSpan timelineBase = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(hlsDir);

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("No ffmpeg encoder is configured; cannot stream channel {Name}", channel.Name);
            return;
        }

        // The rolling window is fixed at 5 minutes. Jellyfin's reader stays at the live edge (it is not
        // realtime-paced), so a larger window buys nothing; a smaller one risks dropping the reader during
        // worst-case self-heal (each restart resumes where its predecessor's output ended and only tops the lead
        // back up to the head start, but a restart whose position cannot be trusted falls back to a full burst).
        // Disk cost is bounded at roughly bitrate x 5 minutes per active channel.
        var listSize = StreamArguments.SegmentsForWindow(5);
        var args = StreamArguments.BuildHlsSegmenter(Path.Combine(hlsDir, "seg%d.ts"), Path.Combine(hlsDir, "stream.m3u8"), listSize, startSegmentNumber);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogInformation("Live Channels: HLS segmenter [{Name}]: {Ffmpeg} {Args}", channel.Name, ffmpeg, string.Join(' ', args));
        stats?.AppendLog("HLS segmenter\n$ ffmpeg " + string.Join(' ', args));

        using var segmenter = new Process { StartInfo = startInfo };
        try
        {
            segmenter.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the HLS segmenter for {Name}", channel.Name);
            return;
        }

        // The segmenter only copies, so its speed is never the bottleneck and it needs no stats sink.
#pragma warning disable CA2025 // The finally block below awaits stderrTask (bounded) before the using scope disposes the segmenter.
        var stderrTask = ReadStandardErrorAsync(segmenter, null, cancellationToken);
#pragma warning restore CA2025
        try
        {
            // The per-item/concat producer and the slate all write to this one stream, so the segmenter receives
            // a single unbroken TS feed it can package into the playlist.
            await StreamToAsync(channel, segmenter.StandardInput.BaseStream, cancellationToken, stats, timelineBase).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                segmenter.StandardInput.Close();
            }
            catch (Exception)
            {
                // The segmenter may already have exited; nothing to flush.
            }

            // With its input closed the segmenter finishes the segment it is writing and exits on its own; give it
            // that moment before forcing it.
            await KillAndWaitAsync(segmenter, graceful: true).ConfigureAwait(false);
            var stderr = await AwaitStderrAsync(stderrTask).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("Live Channels: HLS segmenter ({Name}): {Error}", channel.Name, stderr.Trim());
            }

            stats?.AppendLog("HLS segmenter: closed" + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr.Trim()));
        }
    }

    // Streams the channel as ONE continuous ffmpeg using the concat demuxer, so item boundaries are seamless
    // (no timestamp or continuity reset). Software decode unless every item shares one resolution (a hardware
    // decoder cannot follow a mid-playlist resolution change), with the hardware encoder.
    private async Task StreamConcatAsync(string ffmpeg, Channel channel, ProgramsFollower follower, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan timelineBase = default, DateTime anchorUtc = default, TimeSpan headCredit = default)
    {
        // The position is computed against the FULL program cycle, exactly as the guide computes it, so the stream
        // airs what the guide says is on. Missing files are not filtered out up front (that would both shift every
        // position after a missing item away from the guide and stat every file on every tune-in); a missing item
        // is skipped only when playback reaches it (see FirstPlayable).
        var listFile = Path.Combine(
            Path.GetTempPath(),
            "livechannels-" + channel.Number.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
                (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
                ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);
            var video = _encoders.ResolveVideo(videoCodec, allowHardware: true);
            var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);

            // Hardware-decode the playlist only when every item is the same resolution: the concat demuxer feeds
            // one shared decoder, and a mid-stream resolution change fails a hardware decoder (software handles
            // it fine). On any quick failure, drop to software decode for the rest of the session.
            var programs = follower.Programs;
            var decodeHwaccel = IsUniform(programs) ? video.DecodeHwaccel : null;

            // One continuous ffmpeg. If it dies after producing output (a bad file mid-playlist, an encoder
            // hiccup), restart where its output ended so the channel self-heals rather than going silent. A
            // near-instant exit means the pipeline itself is broken: show standby instead of spinning.
            // A run that keeps dying shortly after starting is the SAME failure on repeat (e.g. an undecodable
            // item at the current schedule position, which the restart re-seeks straight back into): after a few
            // strikes drop hardware decode, and after a few more stop thrashing and show standby, rather than
            // relaunching an encoder every few seconds for as long as the bad item stays on the schedule.
            var shortRuns = 0;
            var headStart = StreamArguments.InitialBurstSeconds;
            var timeline = timelineBase;
            var first = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                // A rebuilt loop (picked up by the watcher below, or noticed now) takes effect from this run.
                _ = follower.TryTakeNewer(TimeSpan.Zero);
                if (!ReferenceEquals(programs, follower.Programs))
                {
                    programs = follower.Programs;
                    if (decodeHwaccel is not null && !IsUniform(programs))
                    {
                        decodeHwaccel = null;
                    }
                }

                // Where on the schedule this run starts, and how much it bursts. The first run starts where the
                // tune-in card ends (or where a restarted session's output ended) and bursts the head start minus
                // any lead it already has. A later run resumes where the previous run's output actually ended,
                // which ran ahead of the wall clock by up to the head start, and tops that lead back up; a reading
                // that is implausible (or missing) falls back to plain "now" with the full head start.
                var now = DateTime.UtcNow;
                var ahead = first ? headCredit : anchorUtc + timeline - now;
                if (ahead < TimeSpan.Zero || ahead > TimeSpan.FromSeconds(2 * headStart))
                {
                    ahead = TimeSpan.Zero;
                }

                var target = first ? anchorUtc + timelineBase : now + ahead;
                var burst = Math.Clamp(headStart - ahead.TotalSeconds, 0, headStart);
                first = false;

                // Start the list AT the item now airing and seek only within it. A single -ss spanning the
                // whole loop makes the concat demuxer (wrapped in -stream_loop) grind through every prior item
                // to reach the target -- observed as minutes of full-tilt decode against the 20-second tune-in
                // deadline on a channel deep into its loop. The playlist is a cycle, so rotating it preserves
                // the exact play order while turning the seek into an index hop inside the first file. The list
                // is rewritten every (re)start because the rotation point is the current schedule position.
                // The concat demuxer reads "file '<path>'" lines; single quotes in a path escape as '\''.
                var (airing, airingOffset) = ScheduleCalculator.CurrentProgram(programs, target, ScheduleCalculator.AnchorFor(programs));

                // The item airing now may be missing (deleted or on unmounted storage): start at the next one that
                // exists instead, and leave missing items out of the list (the concat demuxer cannot open one, and
                // under -stream_loop that sends it back to the top of the list). Only the stretch of the list a
                // viewer can realistically reach is checked (see ConcatList), so a tune-in of a channel with
                // thousands of items stats a handful of files, not all of them.
                var (index, intoItem, skipped) = FirstPlayable(programs, airing, airingOffset, File.Exists);
                if (index < 0)
                {
                    _logger.LogWarning("Every item on channel {Name} is unreachable; showing standby", channel.Name);
                    await StreamSlateAsync(ffmpeg, timeline, output, cancellationToken, stats).ConfigureAwait(false);
                    break;
                }

                if (skipped > 0)
                {
                    _logger.LogWarning("Channel {Name}: skipped {Count} missing item(s) at the current position", channel.Name, skipped);
                }

                await File.WriteAllLinesAsync(
                    listFile,
                    ConcatList(programs, index, File.Exists, ConcatCheckHorizon).Select(p => "file '" + p.Path!.Replace("'", "'\\''", StringComparison.Ordinal) + "'"),
                    cancellationToken).ConfigureAwait(false);

                var args = StreamArguments.BuildConcat(listFile, intoItem, width, bitrate, video, audioEncoder, audioBitrate, decodeHwaccel, burst, timeline);

                // The run is cancelled early (separately from the session) when the loop is rebuilt under it, at the
                // boundary where the rebuilt order takes over.
                var started = DateTime.UtcNow;
                var tracker = new SpeedTracker(timeline.TotalSeconds);
                long total;
                double lastOut;
                bool rebuiltMidRun;
                using (var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
#pragma warning disable CA2025 // The watcher is awaited below, before the using scope disposes the run's token source.
                    var watcher = WatchForRebuildAsync(follower, tracker, anchorUtc, channel.Name, run);
#pragma warning restore CA2025
                    (total, lastOut, _) = await RunFfmpegAsync(ffmpeg, args, channel.Name, output, run.Token, stats, tracker).ConfigureAwait(false);
                    rebuiltMidRun = run.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                    await run.CancelAsync().ConfigureAwait(false);
                    await watcher.ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var ranFor = DateTime.UtcNow - started;

                // A restart appends to the stream the reader is already following, so it must continue the
                // timeline rather than rewind it: pick up where this run's output actually ended (its final
                // progress timestamp on the channel timeline), falling back to the wall clock it ran for when no
                // reading was parsed.
                timeline = NextTimeline(timeline, ranFor, lastOut);
                if (rebuiltMidRun)
                {
                    _logger.LogInformation("Channel {Name}: the schedule was rebuilt; continuing on the new schedule", channel.Name);
                    continue;
                }

                var quickFail = total == 0 || ranFor < TimeSpan.FromSeconds(2);
                if (quickFail && decodeHwaccel is not null)
                {
                    // Hardware decode could not follow this playlist; fall back to software decode and retry
                    // rather than giving up.
                    _logger.LogWarning("Channel {Name}: hardware-decode continuous stream failed; falling back to software decode", channel.Name);
                    decodeHwaccel = null;
                    continue;
                }

                if (quickFail)
                {
                    _logger.LogWarning("Channel {Name}: continuous stream failed to run; showing standby", channel.Name);
                    await StreamSlateAsync(ffmpeg, timeline, output, cancellationToken, stats).ConfigureAwait(false);
                    break;
                }

                if (ranFor < TimeSpan.FromSeconds(60))
                {
                    shortRuns++;
                    if (shortRuns == 3 && decodeHwaccel is not null)
                    {
                        _logger.LogWarning("Channel {Name}: continuous stream keeps dying; retrying with software decode", channel.Name);
                        decodeHwaccel = null;
                        continue;
                    }

                    if (shortRuns >= 6)
                    {
                        _logger.LogWarning("Channel {Name}: continuous stream keeps dying; showing standby", channel.Name);
                        await StreamSlateAsync(ffmpeg, timeline, output, cancellationToken, stats).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    // A healthy long run: earlier strikes were transient, not a stuck failure.
                    shortRuns = 0;
                }

                // Jellyfin keeps reading the same growing file, so a fresh ffmpeg resumes seamlessly where this
                // one's output ended.
                _logger.LogDebug("Channel {Name}: continuous stream ended; resuming at the current position", channel.Name);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(listFile))
                {
                    File.Delete(listFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete concat list {File}", listFile);
            }
        }
    }

    // Watches a running continuous producer for a rebuilt loop. The concat demuxer plays one fixed playlist, so a
    // rebuild only reaches the viewer through a restart: when one appears, this ends the run exactly where the item
    // its output is in ends on the rebuilt schedule (the producer reads at realtime once its head start is in, so
    // that boundary is the same distance away in wall time), and the restart picks the rebuilt order up there.
    private async Task WatchForRebuildAsync(ProgramsFollower follower, SpeedTracker tracker, DateTime anchorUtc, string label, CancellationTokenSource run)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RebuildPollInterval, run.Token).ConfigureAwait(false);
                if (follower.TryTakeNewer(TimeSpan.Zero) is not { } rebuilt)
                {
                    continue;
                }

                var produced = tracker.LastContentSeconds;
                var wait = TimeSpan.Zero;
                if (produced >= 0)
                {
                    var position = anchorUtc + TimeSpan.FromSeconds(produced);
                    var (index, offset) = ScheduleCalculator.CurrentProgram(rebuilt, position, ScheduleCalculator.AnchorFor(rebuilt));
                    wait = TimeSpan.FromTicks(rebuilt[index].DurationTicks) - offset;
                }

                _logger.LogDebug("Channel {Name}: schedule rebuilt; switching over in {Seconds:F0}s at the next boundary", label, wait.TotalSeconds);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, run.Token).ConfigureAwait(false);
                }

                await run.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended (or the session closed) first.
        }
    }

    // How often a continuous producer checks whether its loop was rebuilt.
    private static readonly TimeSpan RebuildPollInterval = TimeSpan.FromSeconds(60);


    /// <summary>
    /// Finds where playback starts: the airing item at its offset when its file exists, otherwise the start of the
    /// next item whose file does. Files are checked one at a time from the airing item onward, stopping at the
    /// first that exists, so a healthy channel stats exactly one file per (re)start rather than every item.
    /// </summary>
    /// <param name="programs">The full program cycle.</param>
    /// <param name="index">The airing item's index in the cycle.</param>
    /// <param name="offset">How far into the airing item the clock is.</param>
    /// <param name="exists">Whether a media file exists.</param>
    /// <returns>The item and offset to start at, and how many missing items were stepped over; the index is <c>-1</c> when no item exists.</returns>
    internal static (int Index, TimeSpan Offset, int Skipped) FirstPlayable(IReadOnlyList<ProgramEntry> programs, int index, TimeSpan offset, Func<string, bool> exists)
    {
        for (var skipped = 0; skipped < programs.Count; skipped++)
        {
            var candidate = (index + skipped) % programs.Count;
            var path = programs[candidate].Path;
            if (!string.IsNullOrEmpty(path) && exists(path))
            {
                return (candidate, skipped == 0 ? offset : TimeSpan.Zero, skipped);
            }
        }

        return (-1, TimeSpan.Zero, programs.Count);
    }

    /// <summary>
    /// The concat playlist for one run: the cycle rotated to start at the given item, skipping items with no path.
    /// Items whose file is missing are left out too, but files are only checked until the listed items cover
    /// <paramref name="horizon"/> of airtime; the rest are listed unchecked. Every restart rebuilds the list from
    /// the current position, so the checked stretch always lies just ahead of the viewer.
    /// </summary>
    /// <param name="programs">The full program cycle.</param>
    /// <param name="startIndex">The item to start at.</param>
    /// <param name="exists">Whether a media file exists.</param>
    /// <param name="horizon">How much airtime ahead to check.</param>
    /// <returns>The items to list, in play order.</returns>
    internal static List<ProgramEntry> ConcatList(IReadOnlyList<ProgramEntry> programs, int startIndex, Func<string, bool> exists, TimeSpan horizon)
    {
        var list = new List<ProgramEntry>(programs.Count);
        long covered = 0;
        foreach (var program in Rotate(programs, startIndex))
        {
            if (string.IsNullOrEmpty(program.Path) || (covered < horizon.Ticks && !exists(program.Path)))
            {
                continue;
            }

            list.Add(program);
            covered += program.DurationTicks;
        }

        return list;
    }

    // How much airtime ahead of the current position a concat run's playlist is checked for missing files.
    internal static readonly TimeSpan ConcatCheckHorizon = TimeSpan.FromHours(24);

    // Rotates the playlist to start at the given item. The loop is a cycle, so the cyclic play order is
    // untouched; only the entry point moves.
    internal static IEnumerable<ProgramEntry> Rotate(IReadOnlyList<ProgramEntry> items, int startIndex)
        => items.Skip(startIndex).Concat(items.Take(startIndex));

    // Streams item-by-item (one ffmpeg per item, timestamps stitched with -output_ts_offset). Used only for
    // subtitle burn-in, high-resolution, HDR, or GPU-upload encoders, which need a per-item pipeline the concat
    // path cannot provide. Every item cold-starts at its boundary; the wall clock a cold start costs is healed by
    // that producer's deficit burst, and the viewer rides the tune-in head start well behind the live edge, so
    // boundaries stay invisible without ever running two encoders at once.
    private async Task StreamPerItemLoopAsync(string ffmpeg, Channel channel, IStreamSchedule schedule, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan timelineBase = default, TimeSpan headCredit = default)
    {
        var consecutiveFailures = 0;
        var itemsPlayed = 0;
        var timeline = timelineBase;

        // The session's wall-clock anchor for catch-up bursts. Each per-item ffmpeg re-anchors its -readrate
        // schedule at its own start, so time lost BETWEEN processes (cold starts, retries) never self-heals at
        // realtime. Every producer therefore bursts the session's current deficit against this anchor: the first
        // item's deficit is exactly the full head start (less any lead a restarted session still has), later
        // items' exactly the wall clock the boundaries lost.
        var sessionStart = DateTime.UtcNow;
        var headStart = StreamArguments.InitialBurstSeconds;
        double BurstNow() => StreamArguments.BurstForDeficit(DateTime.UtcNow - sessionStart, timeline - timelineBase + headCredit, headStart);

        while (!cancellationToken.IsCancellationRequested)
        {
            var step = schedule.Current;
            var program = step.Program;
            _logger.LogDebug("Channel {Name}: item {Number} \"{Title}\"", channel.Name, itemsPlayed + 1, program.Title);

            var (streamed, endSeconds) = await StreamItemAsync(ffmpeg, channel, program, step.Offset, timeline, output, BurstNow, cancellationToken, stats, step.DurationLimit).ConfigureAwait(false);

            if (streamed)
            {
                // Chain the next item's timeline from where this one ACTUALLY ended (the producer's final
                // output timestamp), not from the scheduled boundary: content is already continuous across
                // the seam, so scheduled anchoring only injects a PTS jump the size of the metadata drift --
                // a jump hardware video decoders (VLCKit in Swiftfin) can stall on. The schedule advance is
                // the fallback when no usable reading exists, so skipped items still can't desync the offset.
                var played = step.DurationLimit ?? (TimeSpan.FromTicks(program.DurationTicks) - step.Offset);
                timeline = NextTimeline(timeline, played > TimeSpan.Zero ? played : TimeSpan.Zero, endSeconds);
                itemsPlayed++;
                consecutiveFailures = 0;
            }
            else if (++consecutiveFailures >= Math.Min(schedule.PoolCount, MaxConsecutiveFailures))
            {
                // Enough distinct items failed in a row that the storage is almost certainly the problem, not
                // the items: show standby now instead of walking a huge playlist (the cap keeps a 500-item
                // channel from spinning through spawn-and-fail for minutes before giving the viewer anything).
                _logger.LogWarning("{Count} consecutive item(s) on channel {Name} failed to play; showing standby", consecutiveFailures, channel.Name);
                await StreamSlateAsync(ffmpeg, timeline, output, cancellationToken, stats).ConfigureAwait(false);
                break;
            }
            else
            {
                // A missing item returns instantly; pause briefly so a run of them can't busy-spin the CPU.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            schedule.Advance();
        }

        _logger.LogDebug("Channel {Name}: stream ended after {Items} item(s)", channel.Name, itemsPlayed);
    }

    // Logs, once per tune-in, exactly how the channel will be encoded — resolution, codec, the resolved
    // hardware/software encoder, and whether decoding is hardware-assisted — so the active pipeline is visible
    // in the logs without enabling debug output.
    private void LogEncodePlan(Channel channel, int itemCount, int startIndex, bool perItem, bool uniform, bool hasHdr)
    {
        var (width, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c => (c.TranscodeWidth, c.VideoCodec, c.AudioCodec))
            ?? (1280, Models.VideoCodec.H264, Models.AudioCodec.Aac);
        var height = StreamArguments.OutputHeight(width);
        var profile = _encoders.ResolveVideo(videoCodec, allowHardware: true);
        var (audioEncoder, _) = EncoderResolver.ResolveAudio(audioCodec);

        // Decode follows the server's hardware acceleration. The decode plan is decided PER ITEM, so the label
        // must not overstate the channel-level flags: on the GPU-resident pipeline subtitle burn-in stays on the
        // GPU (overlay_vaapi composite); elsewhere burn-in and HDR route items to software decode. The continuous
        // pipeline hardware-decodes only when every item is the same resolution, and every path falls back to
        // software when a hardware decode fails.
        var burnIn = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never;
        var pipeline = perItem ? "per-item" : "continuous";
        var burnInForcesSoftware = burnIn && !string.IsNullOrEmpty(profile.DecodeDownload);
        var intelHardware = profile.Name.Contains("qsv", StringComparison.Ordinal) || profile.Name.Contains("vaapi", StringComparison.Ordinal);
        var gpuCapable = intelHardware && !string.IsNullOrEmpty(profile.GpuDevice);
        var hwDecode = !hasHdr && profile.DecodeHwaccel is not null && (perItem ? !burnInForcesSoftware : uniform);
        string decode;
        if (perItem && gpuCapable)
        {
            decode = burnIn ? "vaapi (GPU-resident, subtitles composited on GPU)" : "vaapi (GPU-resident)";
        }
        else if (hasHdr)
        {
            decode = "software (HDR)";
        }
        else
        {
            decode = hwDecode ? profile.DecodeHwaccel! : "software";
        }

        _logger.LogInformation(
            "Live Channels: streaming {Name} ({Items} items) at {Width}x{Height} via {Encoder} ({Mode} encode, {Decode} decode, {Pipeline}), audio {Audio}, from item {Index}/{Items}",
            channel.Name,
            itemCount,
            width,
            height,
            profile.Name,
            profile.IsHardware ? "hardware" : "software",
            decode,
            pipeline,
            audioEncoder,
            startIndex + 1,
            itemCount);
    }

    // Streams one item to the output, cold-starting its ffmpeg. The deficit burst is a callback so it is
    // evaluated at the moment each producer actually starts (the software retry re-evaluates it: the failed
    // hardware attempt burned wall clock that retry should recover). Returns whether the item produced output
    // and the producer's final output timestamp in seconds (-1 when none was parsed), which the loop chains
    // the next item's timeline from.
    private async Task<(bool Streamed, double EndSeconds)> StreamItemAsync(string ffmpeg, Channel channel, ProgramEntry program, TimeSpan offset, TimeSpan timeline, Stream output, Func<double> burstSeconds, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan? durationLimit = null)
    {
        if (string.IsNullOrEmpty(program.Path) || !File.Exists(program.Path))
        {
            _logger.LogWarning("Skipping missing media for {Title}", program.Title);
            return (false, -1);
        }

        var subtitle = ChannelService.FindBurnInSubtitle(program, channel.SubtitleBurnIn);

        // Burn the file Jellyfin extracted into its own subtitle cache, the same source its transcodes burn, not
        // the media file itself. The libass `subtitles` filter reads its source from the start to reach the
        // current position, which is fatal on a deep tune-in seek into a multi-GB file (it scans gigabytes while
        // the producer stalls), and the extracted file keeps the track's own markup, so bold, italic, and colour
        // tags survive into the picture.
        //
        // While that extraction is still warming, an item playing from its start can still burn straight out of
        // the media file (at position zero there is nothing to scan through); a seeked tune-in skips the burn for
        // this one item and picks it up next time. Bitmap subtitles are composited from the mapped stream either
        // way, so they never take this path.
        string? subtitlePath = null;
        string? subtitleFonts = null;
        if (subtitle is { IsText: true })
        {
            var file = await _channels.TryResolveBurnInSubtitleAsync(program.ItemId, subtitle.Value.RelativeIndex, cancellationToken).ConfigureAwait(false);
            subtitlePath = file?.Path;
            subtitleFonts = file?.FontsDirectory;

            // The media-file fallback addresses the track by its index INSIDE the file, which only exists for an
            // embedded track: a subtitle that lives in a file beside the media has no such index, so without the
            // resolved path there is nothing to burn.
            var external = program.Subtitles.FirstOrDefault(s => s.RelativeIndex == subtitle.Value.RelativeIndex)?.IsExternal ?? false;
            if (subtitlePath is null && (offset > TimeSpan.Zero || external))
            {
                _logger.LogDebug("Channel {Name}: burn-in subtitle not ready; skipping it on this item", channel.Name);
                subtitle = null;
            }
        }

        var (args, hardwareDecode) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: false, program.IsHdr, program.DefaultAudioOrdinal, burstSeconds(), durationLimit, subtitleFonts);
        var (total, lastOut, exitCode) = await RunFfmpegAsync(ffmpeg, args, program.Title, output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds)).ConfigureAwait(false);

        // The per-item path has no continuous decoder to fall back, so retry a hardware-decode that produced
        // nothing (e.g. a codec the GPU can't decode) once in software, so one bad item can't blank the channel.
        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: true, program.IsHdr, program.DefaultAudioOrdinal, burstSeconds(), durationLimit, subtitleFonts);
            (total, lastOut, exitCode) = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds)).ConfigureAwait(false);
        }

        // A burn-in tune-in seek runs with -copyts, under which ffmpeg measures its progress from the first
        // timestamp it happens to report rather than from the output's start, so the final reading falls short of
        // the true end by an unknown amount. Chain such an item from the schedule instead of that reading.
        if (offset > TimeSpan.Zero && subtitle.HasValue)
        {
            lastOut = -1;
        }

        // A clean (exit 0), uncancelled run of a WHOLE item (no tune-in seek) reveals the item's true
        // playable length. When it drifts from the metadata runtime the schedule was built on, record it so
        // the next guide refresh schedules the real length -- closing the timestamp gap (or overlap) at this
        // item's seam. The exit gate matters: a producer that crashed mid-item produced output too, and its
        // partial length must not be recorded as truth.
        if (total > 0 && exitCode == 0 && offset == TimeSpan.Zero && durationLimit is null && !cancellationToken.IsCancellationRequested)
        {
            var observed = ObservedItemSeconds(lastOut, timeline);
            var metadataSeconds = TimeSpan.FromTicks(program.DurationTicks).TotalSeconds;
            if (observed is { } seconds && Math.Abs(seconds - metadataSeconds) > 2)
            {
                _channels.RecordObservedDuration(program.ItemId, seconds, program.Title, metadataSeconds);
            }
        }

        return (total > 0, lastOut);
    }

    // Streams a standby slate (colour bars + silence), looped, until the client disconnects. Shown when a
    // channel has no playable content so viewers get an intentional standby card, not a black screen. Starts on
    // the channel timeline where the caller left off (after any items that did play), so timestamps never regress.
    // It encodes the channel's configured codecs (in software: a static card costs next to nothing, and it must
    // not depend on the hardware path that may be why content failed), so the copy-mode segmenter's playlist never
    // switches codec when standby takes over. Its progress keeps the session timeline advancing, so a later
    // producer restart resumes after the slate rather than over it.
    private async Task StreamSlateAsync(string ffmpeg, TimeSpan timelineBase, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);
        var video = _encoders.ResolveVideo(videoCodec, allowHardware: false);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
        const double clipSeconds = 10.0;

        var font = FontLocator.Find();
        var timeline = timelineBase;
        var clips = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var args = StreamArguments.BuildSlate(width, bitrate, clipSeconds, timeline, font, video, audioEncoder, audioBitrate);
            var started = DateTime.UtcNow;

            // Only the first clip's command goes into the session log; the loop repeats it every ten seconds.
            var (total, lastOut, _) = await RunFfmpegAsync(ffmpeg, args, "standby", output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds), quiet: clips++ > 0).ConfigureAwait(false);
            if (total == 0)
            {
                break; // ffmpeg could not even produce the slate; avoid a hot loop.
            }

            // If a clip ended far faster than its length (e.g. ffmpeg failing fast with some output), back off
            // briefly so the loop can't spin.
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(clipSeconds / 2))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Chain the next clip from where this one actually ended, as the items do.
            timeline = NextTimeline(timeline, TimeSpan.FromSeconds(clipSeconds), lastOut);
        }
    }

    // Runs ffmpeg with the given arguments, pumping its stdout to the output stream. Returns the bytes
    // written (zero means the item produced nothing -- a real failure, logged with ffmpeg's stderr) and the
    // last output timestamp on the channel timeline (-1 when no progress was parsed), read from the tracker,
    // which must be based at the timeline position this producer's output starts at. The per-item path uses it
    // to chain the next item and to observe an item's true playable length. A quiet run keeps its command and
    // exit summary out of the session log (the looping slate would otherwise repeat them every few seconds).
    private async Task<(long Total, double LastOutSeconds, int ExitCode)> RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, string label, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, SpeedTracker? tracker = null, bool quiet = false)
    {
        tracker ??= new SpeedTracker();
        var (process, stderrTask) = StartProducer(ffmpeg, args, label, quiet ? null : stats, stats, cancellationToken, tracker);
        if (process is null)
        {
            return (0, -1, -1);
        }

        var (total, exitCode) = await PumpProducerAsync(process, stderrTask, label, output, cancellationToken, quiet ? null : stats).ConfigureAwait(false);
        return (total, tracker.LastContentSeconds, exitCode);
    }

    // Starts a producer ffmpeg and immediately begins draining its stderr (so it can never block on a full stderr
    // pipe), but does NOT read its stdout; PumpProducerAsync does that. Returns a null process if it could not start.
    // The log sink receives the command line; the progress sink receives the live speed and timeline.
    private (Process? Process, Task<string> Stderr) StartProducer(string ffmpeg, IReadOnlyList<string> args, string label, SessionStats? stats, SessionStats? progress, CancellationToken cancellationToken, SpeedTracker? tracker = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // The exact producer command, at debug level: enable debug logging for the plugin to diagnose a
        // failure on a specific source (the downstream transcoder error only ever says the temp file was empty).
        _logger.LogDebug("Live Channels: producer ffmpeg [{Label}]: {Ffmpeg} {Args}", label, ffmpeg, string.Join(' ', args));

        // And into the session's reviewable ffmpeg log for the Sessions tab.
        stats?.AppendLog(label + "\n$ ffmpeg " + string.Join(' ', args));

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ffmpeg for {Label}", label);
            process.Dispose();
            return (null, Task.FromResult(string.Empty));
        }

#pragma warning disable CA2025 // The process and its reader task are handed to PumpProducerAsync, which awaits the task (bounded) before it disposes the process.
        return (process, ReadStandardErrorAsync(process, progress, cancellationToken, tracker));
#pragma warning restore CA2025
    }

    // Pumps a started producer's stdout into the output stream until it ends, then kills and disposes it.
    // Returns the bytes written and the producer's exit code (-1 when it had to be killed).
    private async Task<(long Total, int ExitCode)> PumpProducerAsync(Process process, Task<string> stderrTask, string label, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        long total = 0;
        var exitCode = -1;
        var buffer = new byte[BufferSize];
        try
        {
            var naturalEnd = false;
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                naturalEnd = true;
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client went away; stop quietly.
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Stream write ended for {Label}", label);
            }
            finally
            {
                // ffmpeg closes its output while it is still writing the trailer, printing its final progress, and
                // releasing decoders, so at end of output it is given a moment to exit on its own: killed there, a
                // clean item would report a kill instead of exit 0 and lose its final progress reading. A cancelled
                // or failed pump kills straight away.
                await KillAndWaitAsync(process, graceful: naturalEnd).ConfigureAwait(false);
            }

            var stderr = await AwaitStderrAsync(stderrTask).ConfigureAwait(false);
            exitCode = process.HasExited ? process.ExitCode : -1;
            if (total == 0 && !cancellationToken.IsCancellationRequested)
            {
                // Output of nothing is a real failure (e.g. ffmpeg rejected a stream); surface its error and code.
                _logger.LogWarning("ffmpeg produced no output for {Label} (exit {Exit}): {Error}", label, exitCode, stderr.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg ({Label}, {Bytes} bytes, exit {Exit}): {Error}", label, total, exitCode, stderr.Trim());
            }

            // The exit summary (and any stderr tail) joins the session's reviewable ffmpeg log.
            var tail = stderr.Trim();
            stats?.AppendLog(label + ": exit " + exitCode.ToString(CultureInfo.InvariantCulture)
                + ", " + total.ToString(CultureInfo.InvariantCulture) + " bytes"
                + (tail.Length > 0 ? "\n" + tail : string.Empty));
        }
        finally
        {
            process.Dispose();
        }

        return (total, exitCode);
    }

    /// <summary>
    /// Builds the exact production producer arguments for one item, bounded to a fixed slice of content. The
    /// stress test runs N of these concurrently — the same command a real channel runs, pacing included — to
    /// measure how many simultaneous streams the server sustains at full frame rate.
    /// </summary>
    /// <param name="path">The media file path.</param>
    /// <param name="sourceHeight">The video height, driving the same decode decisions a channel makes.</param>
    /// <param name="isHdr">Whether the item is HDR (PQ/HLG).</param>
    /// <param name="offset">Seek offset into the item, so concurrent copies read different sections.</param>
    /// <param name="duration">How much content to encode.</param>
    /// <returns>The ffmpeg argument list and whether it hardware-decodes.</returns>
    public (List<string> Args, bool HardwareDecode) BuildStressArguments(string path, int sourceHeight, bool isHdr, TimeSpan offset, TimeSpan duration)
        => BuildArguments(path, offset, TimeSpan.Zero, null, sourceHeight, null, softwareDecode: false, isHdr, null, initialBurstSeconds: 0, duration);

    private (List<string> Args, bool HardwareDecode) BuildArguments(string path, TimeSpan offset, TimeSpan timeline, (int RelativeIndex, bool IsText)? forcedSubtitle, int sourceHeight, string? externalSubtitlePath, bool softwareDecode, bool isHdr, int? audioOrdinal, double initialBurstSeconds, TimeSpan? durationLimit = null, string? subtitleFontsDir = null)
    {
        // Read just the scalars we need inside the config lock, rather than holding a reference to the live
        // (mutable, shared) configuration object after the lock releases.
        var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);
        var subtitleStyle = ResolveSubtitleStyle();

        // The hardware encoder still applies to burn-in; only the decode side cares about the overlay -- and
        // the GPU-resident Intel pipeline composites the overlay on the GPU (overlay_vaapi), so burn-in there
        // keeps full hardware. Elsewhere, burn-in at 1080p-or-below drops to the software encoder (the CPU
        // composite dominates anyway); above 1080p the hardware encoder stays worth it.
        var hwProfile = _encoders.ResolveVideo(videoCodec, allowHardware: true);
        var hwIsIntelGpu = (hwProfile.Name.Contains("qsv", StringComparison.Ordinal) || hwProfile.Name.Contains("vaapi", StringComparison.Ordinal))
            && !string.IsNullOrEmpty(hwProfile.GpuDevice);
        var allowHardware = !forcedSubtitle.HasValue || sourceHeight > 1080 || hwIsIntelGpu;
        var video = allowHardware ? hwProfile : _encoders.ResolveVideo(videoCodec, allowHardware: false);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
        var intelHardware = video.Name.Contains("qsv", StringComparison.Ordinal) || video.Name.Contains("vaapi", StringComparison.Ordinal);

        // Intel with a known render node (Linux) runs the fully GPU-resident pipeline (built inside Build):
        // decode, deinterlace, scale, tone map, subtitle overlay, and encode all in VRAM, for SDR, 10-bit,
        // interlaced, and HDR alike.
        var intelGpu = intelHardware && !string.IsNullOrEmpty(video.GpuDevice) && !softwareDecode;

        // Outside the GPU-resident pipeline, HDR tone-maps in software (the zscale chain needs software-decoded
        // frames so the 10-bit depth survives), and burn-in forces software when the decoder would keep frames
        // in VRAM (the CPU overlay needs system frames). The caller forces software for the per-item retry.
        var hdrNeedsSoftware = isHdr && !intelGpu;
        var forceSoftware = softwareDecode || hdrNeedsSoftware
            || (forcedSubtitle.HasValue && !intelGpu && !string.IsNullOrEmpty(video.DecodeDownload));

        // Every hardware pipeline reports as such, so a no-output failure retries the item in software
        // (StreamItemAsync) instead of blanking the channel.
        var hardwareDecode = intelGpu || (!forceSoftware && !string.IsNullOrEmpty(video.DecodeHwaccel));
        var args = StreamArguments.Build(path, offset, timeline, width, bitrate, video, audioEncoder, audioBitrate, forcedSubtitle, externalSubtitlePath, forceSoftware, isHdr, audioOrdinal, initialBurstSeconds, durationLimit, subtitleStyle, subtitleFontsDir);
        return (args, hardwareDecode);
    }

    // The configured burn-in appearance as a libass style override, or null when nothing was customised (the
    // subtitle then renders exactly as authored).
    private static string? ResolveSubtitleStyle()
        => Plugin.Instance?.ReadConfiguration(c => SubtitleStyle.Build(
            c.SubtitleFont,
            c.SubtitleFontScalePercent,
            c.SubtitleTextColor,
            c.SubtitleOutlineColor,
            c.SubtitleBorder,
            c.SubtitleBold));

    // Waits for a producer's stderr reader, but not forever: a process stuck in uninterruptible IO (a stale hard
    // NFS mount) can outlive its kill, holding the pipe open, and must not hang the session with it.
    private static async Task<string> AwaitStderrAsync(Task<string> stderrTask)
    {
        try
        {
            return await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadStandardErrorAsync(Process process, SessionStats? stats, CancellationToken cancellationToken, SpeedTracker? tracker = null)
    {
        // Read incrementally so the live encode speed and timeline can be parsed from ffmpeg's progress lines (each
        // terminated by a carriage return) while a long-running producer is still going. Only a bounded tail of
        // stderr is kept for diagnostics, so memory stays flat over a multi-hour stream (the segmenter included).
        var tail = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[512];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\r' || c == '\n')
                    {
                        FlushStderrLine(line, tail, stats, tracker);
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }

            FlushStderrLine(line, tail, stats, tracker);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on close; the tail collected so far is enough.
        }
        catch (IOException)
        {
            // The pipe closed as ffmpeg exited; the tail collected so far is enough.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed after its reader was given up on (see AwaitStderrAsync).
        }

        return tail.ToString();
    }

    // Completes one stderr line: feeds it to the speed tracker, appends it to a bounded diagnostic tail, and
    // resets the line builder. The tail is trimmed from the front so it never grows past a few progress lines.
    private static void FlushStderrLine(StringBuilder line, StringBuilder tail, SessionStats? stats, SpeedTracker? tracker)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        tracker?.Observe(text, stats);

        tail.Append(text).Append('\n');
        const int MaxTail = 4096;
        if (tail.Length > MaxTail)
        {
            tail.Remove(0, tail.Length - MaxTail);
        }
    }

    // Where the next item's timeline starts: the previous item's actual final output timestamp plus one
    // output frame (so the next first PTS lands strictly after the last one), falling back to the scheduled
    // advance when the reading is missing, did not move past the current timeline (a failed or instantly-dead
    // producer), or is implausibly far out (a garbage timestamp must never fling the channel timeline): more than
    // twice the scheduled advance plus a few minutes is no metadata drift, it is a misread.
    internal static TimeSpan NextTimeline(TimeSpan timeline, TimeSpan scheduledAdvance, double producerEndSeconds)
    {
        var scheduled = timeline + scheduledAdvance;
        if (producerEndSeconds <= 0)
        {
            return scheduled;
        }

        var end = TimeSpan.FromSeconds(producerEndSeconds + (1.0 / StreamArguments.OutputFps));
        var ceiling = timeline + scheduledAdvance + scheduledAdvance + TimeSpan.FromMinutes(5);
        return end > timeline && end < ceiling ? end : scheduled;
    }

    // An item's playable length from its producer's final -progress timestamp (on the channel timeline, see
    // SpeedTracker), less the timeline position it started at. Rejects unusable readings (no progress parsed, or a
    // length too short/long to be a real item) by returning null.
    internal static double? ObservedItemSeconds(double lastOutSeconds, TimeSpan timeline)
    {
        if (lastOutSeconds < 0)
        {
            return null;
        }

        var seconds = lastOutSeconds - timeline.TotalSeconds;
        return seconds > 10 && seconds < 86400 ? seconds : null;
    }

    // Computes a live encode speed from ffmpeg's -progress output. ffmpeg's own "speed=" field is a cumulative
    // average since the stream began, so it barely moves once a stream has run a while (and the start-up burst
    // skews it for a long time). Instead this measures the content time advanced (out_time_us) between progress
    // blocks against wall-clock, giving an instantaneous rate that actually tracks whether the box keeps up.
    // One instance per producer process, lightly smoothed so the reading stays steady. Producers run one at a
    // time per session, so only ever one tracker writes the shared SessionStats.
    //
    // ffmpeg's out_time counts from the producer's own first output: it does NOT include -output_ts_offset
    // (verified against Jellyfin's ffmpeg 8.1 and upstream 9.0). The tracker is therefore based at the timeline
    // position the producer's output starts at and adds that base, turning every reading into a position on the
    // channel timeline. (Should a build ever report the offset too, NextTimeline's ceiling rejects the doubled
    // reading and the schedule advance is used instead.)
    internal sealed class SpeedTracker
    {
        private readonly double _baseSeconds;
        private double _prevContentSeconds = -1;
        private long _prevStamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeedTracker"/> class.
        /// </summary>
        /// <param name="baseSeconds">The channel timeline position this producer's output starts at (its -output_ts_offset).</param>
        public SpeedTracker(double baseSeconds = 0)
        {
            _baseSeconds = Math.Max(0, baseSeconds);
        }

        // The last output timestamp parsed from -progress, in seconds on the channel timeline, so an item's
        // playable length is this minus its timeline offset. -1 until a block is parsed. Read from other threads
        // (the continuous producer's rebuild watcher), so it is published with a volatile write.
        public double LastContentSeconds => Volatile.Read(ref _prevContentSeconds);

        public void Observe(string line, SessionStats? stats)
        {
            const string key = "out_time_us=";
            if (!line.StartsWith(key, StringComparison.Ordinal))
            {
                return;
            }

            var value = line.AsSpan(key.Length).Trim();
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) || micros < 0)
            {
                // "out_time_us=N/A" before the first frame, or a malformed block: keep the previous reading.
                return;
            }

            var contentSeconds = _baseSeconds + (micros / 1_000_000.0);

            // Record the position on the channel timeline so a producer restarted under a live viewer knows where
            // to resume the timestamps from.
            stats?.ObserveTimeline(contentSeconds);
            // A monotonic timestamp, so an NTP/VM clock step cannot skew the elapsed time into a false speed.
            var nowStamp = Stopwatch.GetTimestamp();

            if (stats is not null && _prevContentSeconds >= 0)
            {
                var elapsed = (nowStamp - _prevStamp) / (double)Stopwatch.Frequency;
                var advanced = contentSeconds - _prevContentSeconds;
                if (elapsed >= 0.05 && advanced >= 0)
                {
                    var instant = advanced / elapsed;
                    var previous = stats.Speed;
                    // Exponential smoothing keeps the number from flickering between progress blocks.
                    stats.Speed = previous > 0 ? (previous * 0.6) + (instant * 0.4) : instant;
                }
            }

            Volatile.Write(ref _prevContentSeconds, contentSeconds);
            _prevStamp = nowStamp;
        }
    }

    // Kills the ffmpeg process tree and waits for it to actually exit. Kill() only signals; without waiting,
    // the encoder child can linger (still burning CPU) past the next item's spawn, piling up transcodes. A
    // graceful stop (the process already closed its output) first gives it a moment to finish and exit cleanly.
    private async Task KillAndWaitAsync(Process process, bool graceful = false)
    {
        if (graceful)
        {
            try
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Still running after the grace period: fall through to the kill.
            }
            catch (InvalidOperationException)
            {
                // No process is associated (it never started, or is already gone).
                return;
            }
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill ffmpeg process");
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg did not exit promptly after kill");
        }
    }
}
