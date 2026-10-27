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
using MediaBrowser.Common.Configuration;
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

    // How long a producer may go without writing a byte before it is judged hung and killed. A paced producer
    // writes continuously, so a long silence means a wedged hardware encoder or a stalled network read: left
    // alone, the session stays "healthy" while viewers watch a frozen channel, because nothing completes for the
    // self-heal paths to react to. Killing the run hands it to them (the next item, or a resumed concat run).
    // The first byte gets more room than the rest: opening a file on a slow mount, seeking deep into a large
    // item, and reading a text subtitle track out of the file all happen before any output. Once output is
    // flowing the limit has to beat Jellyfin: its delivery ends the viewer's stream about 28 seconds after the
    // producer goes silent (measured on 12.4, segment reserve included), so a hung producer must be killed with
    // enough of that left for its replacement to write the next segments.
    private static readonly TimeSpan FirstOutputTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    // Standby holds for StandbyHoldFirst, doubling per hold up to StandbyHoldCeiling, before the schedule is tried
    // again. The doubling starts over once the schedule has played for StandbyBackoffReset.
    private static readonly TimeSpan StandbyHoldCeiling = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StandbyBackoffReset = TimeSpan.FromMinutes(10);

    private readonly IMediaEncoder _encoder;
    private readonly ChannelService _channels;
    private readonly EncoderResolver _encoders;
    private readonly IntroService _intros;
    private readonly BumperService? _bumpers;
    private readonly ILogger<StreamSessionService> _logger;
    private readonly FailureReports _reports;

    // Where the concat playlists are written: the working folder of the plugin's stream root.
    private readonly string _workDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSessionService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="channels">The channel service, used to resolve and schedule the channel's items.</param>
    /// <param name="encoders">The encoder resolver, used to pick software/hardware encoders.</param>
    /// <param name="intros">The tune-in card cache, played ahead of the first item of a fresh session.</param>
    /// <param name="appPaths">The server paths; failure reports are written to its log directory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="bumpers">The Up Next bumpers, played at the head of every slot of a channel that shows them; <c>null</c> plays black there instead.</param>
    public StreamSessionService(IMediaEncoder encoder, ChannelService channels, EncoderResolver encoders, IntroService intros, IApplicationPaths appPaths, ILogger<StreamSessionService> logger, BumperService? bumpers = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _encoder = encoder;
        _channels = channels;
        _encoders = encoders;
        _intros = intros;
        _bumpers = bumpers;
        _logger = logger;
        _reports = new FailureReports(appPaths.LogDirectoryPath);
        _workDir = PluginPaths.WorkDir(appPaths);
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

        // Fix the session's output settings before anything is written, so the card, the content, and any later
        // standby or restarted producer all encode the format its viewers set up for.
        OutputFor(stats);

        // A fresh session opens on the tune-in card: its bytes go into the segmenter right now, before the
        // schedule is even resolved, so the first segment closes (and the tune-in completes) within a fraction of
        // a second while the real content is seeked and encoded behind it. The first producer then chains its
        // timeline from the card's true end exactly as every item chains from the previous one, and the
        // schedule position it seeks to is advanced by the card's length so the content still lands on the
        // wall clock the guide shows. A restarted producer (non-zero timeline) is continuing under a viewer and
        // never replays the card. A tune-in during an item's Up Next bumper skips the card too: the slot shows black
        // until the item starts, on time, rather than a card running into the item.
        var lead = TimeSpan.Zero;
        var duringBumper = timelineBase == TimeSpan.Zero && DuringBumper(channel, DateTime.UtcNow);
        var card = timelineBase == TimeSpan.Zero && !duringBumper ? _intros.TryGetCached(channel) : null;
        if (duringBumper)
        {
            _logger.LogInformation("Live Channels: {Name}: tuned in during an Up Next bumper; showing black until the item starts", channel.Name);
            stats?.AppendLog("Tune-in during an Up Next bumper: no card, black until the item starts");
        }
        else if (timelineBase == TimeSpan.Zero && card is null)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The card could not be delivered (an unreadable cache file, or the segmenter has already gone):
                // carry on without it; a dead segmenter fails the producer the same way a moment later.
                _logger.LogWarning(ex, "Live Channels: {Name}: could not play the tune-in card; starting on content", channel.Name);
            }
        }

        // The channel plays its schedule until it has nothing it can play (no items resolve, or the library is
        // unreachable), then shows standby for a while and tries the schedule again, rejoining it at the wall
        // clock exactly as a restarted producer does. The hold doubles while the outage lasts (see StandbyHold), so
        // a long outage retries every few minutes instead of every few seconds, and a brief one (a storage blip)
        // costs a viewer a minute of standby instead of the rest of the session.
        var openedOnCard = card is not null;
        var standbyRound = 0;
        while (true)
        {
            var scheduleStarted = DateTime.UtcNow;
            var standby = await StreamScheduleAsync(ffmpeg, channel, output, stats, timelineBase, lead, openedOnCard, cancellationToken).ConfigureAwait(false);
            if (standby is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (standby.Reason is not { } reason)
            {
                // Not a failure: the path chosen at tune-in no longer suits the schedule, so plan again from
                // where the output reached, with no standby in between, on the channel's current settings.
                timelineBase = standby.At;
                lead = TimeSpan.Zero;
                openedOnCard = false;
                if (CurrentChannel(channel) is not { } replanned)
                {
                    return;
                }

                channel = replanned;
                continue;
            }

            if (DateTime.UtcNow - scheduleStarted >= StandbyBackoffReset)
            {
                standbyRound = 0;
            }

            var hold = StandbyHold(standbyRound++, StandbyHoldFirst);
            _logger.LogWarning("Live Channels: {Name}: showing standby for {Seconds:F0}s, then retrying the schedule", channel.Name, hold.TotalSeconds);
            stats?.AppendLog("standby for " + hold.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s, then retrying the schedule");
            var resumeAt = await StreamSlateAsync(ffmpeg, standby.At, output, cancellationToken, stats, hold, StandbyMessage(reason)).ConfigureAwait(false);
            if (resumeAt is null || cancellationToken.IsCancellationRequested)
            {
                // The slate itself could not run: end this producer so the session's own recovery takes over.
                return;
            }

            // Retry on the channel's current settings: an outage is often fixed by editing the channel.
            timelineBase = resumeAt.Value;
            lead = TimeSpan.Zero;
            openedOnCard = false;
            if (CurrentChannel(channel) is not { } retried)
            {
                return;
            }

            channel = retried;
        }
    }

    // The channel as currently configured, or null once it has been deleted or disabled: a session must not keep
    // playing a channel that no longer exists. The session ends, and nothing restarts it (the restart path looks
    // the channel up the same way).
    private Channel? CurrentChannel(Channel channel)
    {
        // Without a loaded configuration (the plugin is not initialised) there is nothing to compare against.
        if (Plugin.Instance is null)
        {
            return channel;
        }

        var current = _channels.FindChannel(channel.Id);
        if (current is null)
        {
            _logger.LogInformation("Live Channels: {Name} was removed or disabled; ending its stream", channel.Name);
        }

        return current;
    }

    /// <summary>
    /// The line the standby card shows under "Standby", saying why the channel is off its schedule, so a viewer
    /// (or the admin they ask) can tell a storage problem from an encoding one. Plain words only: the text goes
    /// into a drawtext filter.
    /// </summary>
    /// <param name="reason">Why the channel is on standby.</param>
    /// <returns>The message.</returns>
    internal static string StandbyMessage(StandbyReason reason) => reason switch
    {
        StandbyReason.NothingToPlay => "This channel has nothing to play right now",
        StandbyReason.MediaUnreachable => "The media for this channel cannot be reached",
        StandbyReason.ItemsFailing => "The media for this channel cannot be played right now",
        StandbyReason.EncoderFailing => "The encoder for this channel keeps failing",
        _ => "Channel content is unavailable"
    };

    /// <summary>
    /// Gets the first standby hold of an outage (one minute). Settable only so tests can shorten it.
    /// </summary>
    internal TimeSpan StandbyHoldFirst { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long standby holds before the schedule is tried again, by how many standby holds in a row this session
    /// has had: the first hold, doubling to a five-minute ceiling.
    /// </summary>
    /// <param name="round">The number of standby holds before this one since the channel last played for a while.</param>
    /// <param name="first">The first hold.</param>
    /// <returns>The standby hold.</returns>
    internal static TimeSpan StandbyHold(int round, TimeSpan first)
        => TimeSpan.FromSeconds(Math.Min(StandbyHoldCeiling.TotalSeconds, first.TotalSeconds * Math.Pow(2, Math.Clamp(round, 0, 16))));

    // Plays the channel's schedule from the timeline position given (where the tune-in card or a previous standby
    // left off) until the session is cancelled, returning null, or until nothing on the schedule can be played,
    // returning the timeline position standby should start from.
    private async Task<StandbyRequest?> StreamScheduleAsync(string ffmpeg, Channel channel, Stream output, SessionStats? stats, TimeSpan timelineBase, TimeSpan lead, bool openedOnCard, CancellationToken cancellationToken)
    {
        // A restarted producer continues under a viewer, and the producer it replaces had run ahead of the wall
        // clock by up to its head start. Resume the schedule where that output actually ended (the session's
        // anchor plus the timeline it reached) rather than at plain "now", which would replay that lead.
        var restarted = timelineBase > TimeSpan.Zero && !openedOnCard;
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
            return new StandbyRequest(timelineBase, StandbyReason.NothingToPlay);
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
        var videoCodec = OutputFor(stats).VideoCodec;
        var usesHwUpload = _encoders.ResolveVideo(videoCodec, allowHardware: true).PixelStage.Contains("hwupload", StringComparison.Ordinal);
        var highRes = programs.Any(p => p.SourceHeight > 1080);

        // Whether any item is HDR (which forces the per-item tone-map path). Each item's HDR flag is probed once at
        // guide refresh and cached on the schedule entry, so this is a pure in-memory scan with no media-stream
        // queries on the tune-in critical path. The short-circuit skips even the scan when the channel is
        // already per-item for another reason (burn-in, a >1080p item, or a GPU-upload encoder).
        var alreadyPerItem = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never || highRes || usesHwUpload;
        var hasHdr = !alreadyPerItem && programs.Any(p => p.IsHdr);

        // A video-only item has no audio packets to feed the concat demuxer's shared audio stream; only the
        // per-item path can give it a silent track of its own.
        var hasVideoOnly = !alreadyPerItem && !hasHdr && programs.Any(p => p.IsVideoOnly);

        // The concat demuxer sets up one video and one audio decoder from the FIRST file and feeds every later
        // file through them, so it can only play a playlist whose items all share their formats (see
        // ConcatCompatible); anything else airs with broken or silent stretches. Those channels stream per item.
        var mixedFormats = !alreadyPerItem && !hasHdr && !hasVideoOnly && !timeOfDay && !ConcatCompatible(programs);
        if (mixedFormats)
        {
            _logger.LogInformation("Live Channels: {Name}: its items differ in video codec, audio codec, audio channels, default audio track, or colour matrix (or were scanned before these were recorded), so it streams item by item", channel.Name);
        }

        // A channel that shows Up Next bumpers plays every slot as bumper then item, which the one continuous concat
        // playlist cannot interleave; the bumper is a deliberate break, so the concat path's seamlessness buys
        // nothing there. Its black filler is made ready up front, since a tune-in during a bumper needs it at once.
        var bumpers = ProgramLoop.LeadOf(programs) > 0;
        if (bumpers && _bumpers is not null)
        {
            _ = _bumpers.EnsureBlackAsync(OutputFor(stats).Width, CancellationToken.None);
        }

        var perItem = alreadyPerItem || hasHdr || hasVideoOnly || mixedFormats || timeOfDay || bumpers;
        var uniform = IsUniform(programs);

        // A fresh session bursts the full head start; a restart only tops up whatever lead it has lost, so the
        // live edge returns to schedule without lurching past it.
        var headCredit = restarted ? lead : TimeSpan.Zero;

        if (!perItem)
        {
            var (concatIndex, _) = ScheduleCalculator.CurrentProgram(programs, startUtc, ScheduleCalculator.AnchorFor(programs));
            LogEncodePlan(channel, programs.Count, concatIndex, perItem, uniform, hasHdr, stats);
            return await StreamConcatAsync(ffmpeg, channel, follower, output, cancellationToken, stats, timelineBase, anchor, headCredit, startUtc).ConfigureAwait(false);
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

        LogEncodePlan(channel, programs.Count, planIndex, perItem, uniform, hasHdr, stats);
        return await StreamPerItemLoopAsync(ffmpeg, channel, schedule, output, cancellationToken, stats, timelineBase, headCredit).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether every item can share the continuous concat pipeline's single pair of decoders: the same video
    /// codec, the same audio codec and channel count on the track each plays, that track being at the same position
    /// in every file (the one concat input maps a single track number for all of them), and the same colour matrix
    /// (the one scale pass converts every item from a single input matrix). Measured on jellyfin-ffmpeg: AAC 5.1 after AAC
    /// stereo, AC-3 after AAC, and HEVC after H.264 all fail to decode, leaving silent or broken stretches with no
    /// error surfacing anywhere; a sample rate change alone decodes fine. An item whose formats were not recorded
    /// (a schedule cached before they were) counts as incompatible, so it takes the per-item path, which reopens
    /// the decoders for every item, until the next guide refresh records them.
    /// </summary>
    /// <param name="programs">The channel's items.</param>
    /// <returns>Whether the concat pipeline can play them all.</returns>
    internal static bool ConcatCompatible(IReadOnlyList<ProgramEntry> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        if (programs.Count == 0)
        {
            return true;
        }

        var first = programs[0];
        if (string.IsNullOrEmpty(first.SourceVideoCodec) || string.IsNullOrEmpty(first.SourceAudioCodec) || first.SourceAudioChannels is null)
        {
            return false;
        }

        foreach (var p in programs)
        {
            // The concat list holds one directive per line and has no escape for a line break, so a file name
            // containing one would split into extra directives: such an item plays through the per-item path.
            if (p.Path is { } path && path.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                return false;
            }

            // The one concat input carries the first file's display rotation for every file, so a rotated item
            // (a phone video) would play sideways or turn the rest of the playlist; it plays per item instead.
            if (p.SourceRotation != 0)
            {
                return false;
            }

            if (!string.Equals(p.SourceVideoCodec, first.SourceVideoCodec, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(p.SourceAudioCodec, first.SourceAudioCodec, StringComparison.OrdinalIgnoreCase)
                || p.SourceAudioChannels != first.SourceAudioChannels
                || (p.DefaultAudioOrdinal ?? 0) != (first.DefaultAudioOrdinal ?? 0)
                || StreamArguments.InputMatrix(p.SourceColorSpace, p.SourceHeight) != StreamArguments.InputMatrix(first.SourceColorSpace, first.SourceHeight))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the continuous concat pipeline can play these items at all: no Up Next bumpers (one playlist cannot
    /// interleave them, so switching them on mid-session hands the stream to the per-item path), none above 1080p
    /// (software decode cannot keep up), none HDR (only the per-item path tone-maps), none without audio (concat
    /// has no track to give them), and all sharing their formats (see <see cref="ConcatCompatible"/>). Channel-level reasons for the
    /// per-item path (burn-in, time-of-day ratings, a GPU-upload encoder) do not change with the schedule.
    /// </summary>
    /// <param name="programs">The channel's items.</param>
    /// <returns>Whether the concat pipeline suits them.</returns>
    internal static bool ConcatSuits(IReadOnlyList<ProgramEntry> programs)
        => ProgramLoop.LeadOf(programs) == 0 && !programs.Any(p => p.SourceHeight > 1080 || p.IsHdr || p.IsVideoOnly) && ConcatCompatible(programs);

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
    // Returns null once the session is cancelled, or the timeline position standby should start from when the
    // playlist cannot be played.
    private async Task<StandbyRequest?> StreamConcatAsync(string ffmpeg, Channel channel, ProgramsFollower follower, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan timelineBase = default, DateTime anchorUtc = default, TimeSpan headCredit = default, DateTime startUtc = default)
    {
        // The position is computed against the FULL program cycle, exactly as the guide computes it, so the stream
        // airs what the guide says is on. Missing files are not filtered out up front (that would both shift every
        // position after a missing item away from the guide and stat every file on every tune-in); a missing item
        // is skipped only when playback reaches it (see FirstPlayable).
        var listFile = Path.Combine(
            _workDir,
            "livechannels-" + channel.Number.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Directory.CreateDirectory(_workDir);
            var (width, bitrate, videoCodec, audioCodec) = OutputFor(stats);
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

                    // The pipeline was chosen for the items at tune-in. A rebuild (the daily guide refresh, a save)
                    // can bring in an item this path cannot play (another format, HDR, 4K, no audio), so it hands
                    // back to the planner, which picks the path again from here.
                    if (!ConcatSuits(programs))
                    {
                        _logger.LogInformation("Live Channels: {Name}: the rebuilt schedule cannot stream continuously; switching to item by item", channel.Name);
                        return new StandbyRequest(timeline, null);
                    }

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

                // The first run starts where the planner placed it (the same instant the per-item path and the
                // tune-in log use). That is the session anchor plus the timeline for a fresh session, but for a
                // restart or a re-plan whose output fell behind the wall clock (minutes on standby or timing out)
                // the old anchor would resume content minutes late and keep the channel that far off the guide.
                var target = first ? (startUtc == default ? anchorUtc + timelineBase : startUtc) : now + ahead;
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
                var storage = new StorageProbe();
                var (index, intoItem, skipped) = FirstPlayable(programs, airing, airingOffset, storage.Exists);
                if (index < 0 || storage.Hung)
                {
                    _logger.LogWarning("Every item on channel {Name} is unreachable{Hung}; showing standby", channel.Name, storage.Hung ? " (the storage is not answering)" : string.Empty);
                    return new StandbyRequest(timeline, StandbyReason.MediaUnreachable);
                }

                if (skipped > 0)
                {
                    _logger.LogWarning("Channel {Name}: skipped {Count} missing item(s) at the current position", channel.Name, skipped);
                }

                var playlist = ConcatList(programs, index, storage.Exists, ConcatCheckHorizon);
                if (storage.Hung)
                {
                    _logger.LogWarning("Channel {Name}: the storage stopped answering while its playlist was checked; showing standby", channel.Name);
                    return new StandbyRequest(timeline, StandbyReason.MediaUnreachable);
                }

                await File.WriteAllLinesAsync(
                    listFile,
                    playlist.Select(p => "file '" + p.Path!.Replace("'", "'\\''", StringComparison.Ordinal) + "'"),
                    cancellationToken).ConfigureAwait(false);

                var args = StreamArguments.BuildConcat(listFile, intoItem, width, bitrate, video, audioEncoder, audioBitrate, decodeHwaccel, burst, timeline, StreamArguments.InputMatrix(programs[0].SourceColorSpace, programs[0].SourceHeight), programs[0].DefaultAudioOrdinal ?? 0);

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
                    // The watcher maps this run's output back to guide time from where the run actually started
                    // (its target at its starting timeline), not the session anchor: after a restart or re-plan
                    // that fell behind, the two differ by the time lost, and the switch-over would land mid-item.
                    var watcher = WatchForRebuildAsync(follower, tracker, target - timeline, channel.Name, run);
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
                    return new StandbyRequest(timeline, StandbyReason.EncoderFailing);
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
                        return new StandbyRequest(timeline, StandbyReason.EncoderFailing);
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

            return null;
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
                    wait = TimeSpan.FromTicks(ProgramLoop.SlotTicks(rebuilt, index)) - offset;
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
    // Returns null once the session is cancelled, or the timeline position standby should start from when item
    // after item fails to play.
    private async Task<StandbyRequest?> StreamPerItemLoopAsync(string ffmpeg, Channel channel, IStreamSchedule schedule, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan timelineBase = default, TimeSpan headCredit = default)
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

        // The rest of an item whose producer died partway through, played next instead of moving on (see below).
        StreamStep? resume = null;

        // Items that stopped partway and then played on in software. Two of them say the hardware decode is what
        // fails on this channel, so the rest of the session decodes in software from the start of each item
        // instead of breaking every one partway.
        var resumedInSoftware = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var resuming = resume.HasValue;
            var step = resume ?? schedule.Current;
            resume = null;
            var program = step.Program;

            // A slot that opens with an Up Next bumper plays it (or black) up to the item's start, then the item
            // from its start; a slot joined past the bumper plays the item from where the clock is. The item never
            // moves: whatever part of the bumper is not shown is black, never skipped ahead of.
            if (!resuming && step.Lead > TimeSpan.Zero)
            {
                var preroll = step.Lead - step.Offset;
                if (preroll > TimeSpan.Zero)
                {
                    timeline = await StreamPrerollAsync(ffmpeg, channel, step, preroll, timeline, output, BurstNow, stats, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                step = ContentStep(step);
                if (step.DurationLimit is { } left && left <= TimeSpan.Zero)
                {
                    schedule.Advance();
                    continue;
                }

                // The next slot's bumper renders while this item plays.
                if (_bumpers is not null && schedule.PeekNext() is { } next)
                {
                    _bumpers.Prepare(channel, next, OutputFor(stats).Width, stats?.LogoPath);
                }
            }
            _logger.LogDebug("Channel {Name}: item {Number} \"{Title}\"", channel.Name, itemsPlayed + 1, program.Title);

            var storage = new StorageProbe();
            var (streamed, endSeconds, cutShortAfter) = await StreamItemAsync(ffmpeg, channel, program, step.Offset, timeline, output, BurstNow, cancellationToken, stats, step.DurationLimit, softwareDecodeOnly: resuming || resumedInSoftware >= 2, storage).ConfigureAwait(false);
            if (storage.Hung)
            {
                // A hung mount fails every item the same way; waiting on each in turn would keep the viewer on a
                // frozen picture for minutes before standby.
                _logger.LogWarning("Channel {Name}: the storage holding \"{Title}\" is not answering; showing standby", channel.Name, program.Title);
                return new StandbyRequest(timeline, StandbyReason.MediaUnreachable);
            }

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
            }

            if (streamed && cutShortAfter is { } cut && !cancellationToken.IsCancellationRequested)
            {
                // The producer wrote some of the item, then failed (a GPU that dies a few seconds in, a stall the
                // watchdog killed). That is a failure, not a play: counted toward standby, so an encoder failing
                // every item cannot walk the whole playlist a few seconds at a time. The rest of the item plays
                // once more, in software, so the channel stays on the guide's item and its schedule; a second
                // failure on the same item moves on.
                // The resume comes first, so even a one-item channel gets its second chance; only a resume that
                // fails too counts toward standby.
                if (!resuming)
                {
                    _logger.LogWarning("Channel {Name}: \"{Title}\" stopped {Seconds:F0}s in; resuming it in software", channel.Name, program.Title, cut.TotalSeconds);
                    stats?.AppendLog("item stopped " + cut.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s in; resuming it in software");
                    resume = ResumeAfter(step, cut);
                    continue;
                }

                if (++consecutiveFailures >= Math.Min(schedule.PoolCount, MaxConsecutiveFailures))
                {
                    _logger.LogWarning("{Count} consecutive item(s) on channel {Name} failed to play; showing standby", consecutiveFailures, channel.Name);
                    return new StandbyRequest(timeline, StandbyReason.ItemsFailing);
                }
            }
            else if (streamed)
            {
                consecutiveFailures = 0;
                if (resuming)
                {
                    resumedInSoftware++;
                }
            }
            else if (++consecutiveFailures >= Math.Min(schedule.PoolCount, MaxConsecutiveFailures))
            {
                // Enough distinct items failed in a row that the storage is almost certainly the problem, not
                // the items: show standby now instead of walking a huge playlist (the cap keeps a 500-item
                // channel from spinning through spawn-and-fail for minutes before giving the viewer anything).
                _logger.LogWarning("{Count} consecutive item(s) on channel {Name} failed to play; showing standby", consecutiveFailures, channel.Name);
                _logger.LogDebug("Channel {Name}: stream paused after {Items} item(s)", channel.Name, itemsPlayed);
                return new StandbyRequest(timeline, StandbyReason.ItemsFailing);
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
        return null;
    }

    /// <summary>
    /// The rest of an item after its producer stopped <paramref name="played"/> into it: the same item, further in,
    /// with any duration limit shortened to match, so it still ends where the schedule ends it.
    /// </summary>
    /// <param name="step">The step that was cut short.</param>
    /// <param name="played">How much of it was written.</param>
    /// <returns>The step that plays the remainder.</returns>
    internal static StreamStep ResumeAfter(StreamStep step, TimeSpan played)
        => step with
        {
            Offset = step.Offset + played,
            DurationLimit = step.DurationLimit is { } limit ? (limit - played > TimeSpan.Zero ? limit - played : TimeSpan.Zero) : null,
        };

    /// <summary>
    /// The part of a slot after its Up Next bumper: the item itself, from its start when the slot was joined before
    /// or during the bumper, else from where the clock is, with any playback cap shortened by the bumper part it
    /// included. A step with no bumper is returned unchanged.
    /// </summary>
    /// <param name="step">The slot, its offset measured from the slot's start.</param>
    /// <returns>The item's step, its offset measured from the item's start.</returns>
    internal static StreamStep ContentStep(StreamStep step)
    {
        if (step.Lead <= TimeSpan.Zero)
        {
            return step;
        }

        var intoItem = step.Offset - step.Lead;
        var preroll = intoItem < TimeSpan.Zero ? -intoItem : TimeSpan.Zero;
        return step with
        {
            Offset = intoItem > TimeSpan.Zero ? intoItem : TimeSpan.Zero,
            DurationLimit = step.DurationLimit is { } limit ? limit - preroll : null,
            Lead = TimeSpan.Zero,
        };
    }

    /// <summary>Whether the guide time falls in an item's Up Next bumper, the head of its slot before the item starts.</summary>
    /// <param name="timeline">The channel's timeline around the time.</param>
    /// <param name="atUtc">The time.</param>
    /// <returns>Whether a bumper is on at that time.</returns>
    internal static bool DuringBumper(IReadOnlyList<ScheduledProgram> timeline, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        foreach (var slot in timeline)
        {
            if (slot.Start <= atUtc && atUtc < slot.Stop)
            {
                return atUtc < slot.ContentStart;
            }
        }

        return false;
    }

    // Whether a tune-in now lands in an Up Next bumper. Resolving the loop here costs the tune-in nothing extra: the
    // schedule is resolved right after the card anyway, and this leaves it in memory for that.
    private bool DuringBumper(Channel channel, DateTime nowUtc)
    {
        try
        {
            var programs = _channels.ResolvePrograms(channel);
            return programs.Count > 0
                && ProgramLoop.LeadOf(programs) > 0
                && DuringBumper(_channels.BuildTimeline(channel, programs, nowUtc, nowUtc.AddTicks(1)), nowUtc);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: {Name}: could not tell whether a bumper is on; opening as usual", channel.Name);
            return false;
        }
    }

    // Plays the head of a slot before its item: the item's Up Next bumper when the slot is played from its start and
    // the bumper was rendered in time, otherwise black, for exactly the time left before the item starts. Returns
    // the timeline the item chains from. Should neither play, the item follows straight on (a little early, which
    // the per-item pacing absorbs) rather than the slot stalling.
    private async Task<TimeSpan> StreamPrerollAsync(string ffmpeg, Channel channel, StreamStep step, TimeSpan length, TimeSpan timeline, Stream output, Func<double> burstSeconds, SessionStats? stats, CancellationToken cancellationToken)
    {
        if (_bumpers is null)
        {
            return timeline;
        }

        var width = OutputFor(stats).Width;
        var bumper = step.Offset <= TimeSpan.Zero ? _bumpers.TryGetCached(channel, step.Program, width, stats?.LogoPath) : null;
        if (bumper is not null)
        {
            var (played, end, _) = await StreamItemAsync(ffmpeg, channel, BumperService.ClipEntry(bumper, "Up Next: " + step.Program.Title, width, BumperService.BumperLength.TotalSeconds), TimeSpan.Zero, timeline, output, burstSeconds, cancellationToken, stats, length, softwareDecodeOnly: true).ConfigureAwait(false);
            if (played || cancellationToken.IsCancellationRequested)
            {
                return played ? NextTimeline(timeline, length, end) : timeline;
            }
        }
        else
        {
            stats?.AppendLog((step.Offset > TimeSpan.Zero ? "Joined partway into" : "No rendered") + " Up Next bumper for \"" + step.Program.Title + "\"; black for " + length.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
        }

        var black = await _bumpers.EnsureBlackAsync(width, cancellationToken).ConfigureAwait(false);
        if (black is not null)
        {
            var (played, end, _) = await StreamItemAsync(ffmpeg, channel, BumperService.ClipEntry(black, "black before " + step.Program.Title, width, BumperService.BumperLength.TotalSeconds + 1), TimeSpan.Zero, timeline, output, burstSeconds, cancellationToken, stats, length, softwareDecodeOnly: true).ConfigureAwait(false);
            if (played)
            {
                return NextTimeline(timeline, length, end);
            }
        }

        _logger.LogWarning("Live Channels: {Name}: could not play the head of \"{Title}\"'s slot; the item starts early", channel.Name, step.Program.Title);
        return timeline;
    }

    /// <summary>
    /// How far into an item its producer got when it failed partway: <c>null</c> for a clean exit, a cancelled
    /// run, a run with no usable end reading, or one that reached within <see cref="CutShortTolerance"/> of the
    /// end it was meant to reach (a nonzero exit at the very end loses nothing worth replaying).
    /// </summary>
    /// <param name="exitCode">The producer's exit code.</param>
    /// <param name="cancelled">Whether the session was cancelled.</param>
    /// <param name="timeline">Where on the channel timeline the item started.</param>
    /// <param name="endSeconds">The output's end on the channel timeline, or negative when unknown.</param>
    /// <param name="intended">How much of the item the run was meant to play.</param>
    /// <returns>How much it played, or <c>null</c>.</returns>
    internal static TimeSpan? CutShort(int exitCode, bool cancelled, TimeSpan timeline, double endSeconds, TimeSpan intended)
    {
        if (exitCode == 0 || cancelled || endSeconds < 0)
        {
            return null;
        }

        var played = TimeSpan.FromSeconds(Math.Max(0, endSeconds - timeline.TotalSeconds));
        return played < intended - CutShortTolerance ? played : null;
    }

    // How close to its intended end a failing producer must get for the item to count as played.
    internal static readonly TimeSpan CutShortTolerance = TimeSpan.FromSeconds(5);

    // The output settings in effect now. Read as scalars inside the config lock, rather than holding a reference
    // to the live (mutable, shared) configuration object after the lock releases.
    private static OutputSettings CurrentOutput()
        => Plugin.Instance?.ReadConfiguration(c => new OutputSettings(c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? new OutputSettings(1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);

    // The output settings a session encodes with: pinned on its stats at its first producer, so a codec or size
    // saved mid-session never switches the stream its viewers are decoding (see OutputSettings).
    internal static OutputSettings OutputFor(SessionStats? stats) => stats?.PinOutput(CurrentOutput) ?? CurrentOutput();

    // Logs, once per tune-in, exactly how the channel will be encoded — resolution, codec, the resolved
    // hardware/software encoder, and whether decoding is hardware-assisted — so the active pipeline is visible
    // in the logs without enabling debug output.
    private void LogEncodePlan(Channel channel, int itemCount, int startIndex, bool perItem, bool uniform, bool hasHdr, SessionStats? stats)
    {
        var (width, _, videoCodec, audioCodec) = OutputFor(stats);
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
    private async Task<(bool Streamed, double EndSeconds, TimeSpan? CutShortAfter)> StreamItemAsync(string ffmpeg, Channel channel, ProgramEntry program, TimeSpan offset, TimeSpan timeline, Stream output, Func<double> burstSeconds, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan? durationLimit = null, bool softwareDecodeOnly = false, StorageProbe? storage = null)
    {
        if (string.IsNullOrEmpty(program.Path) || !(storage ?? new StorageProbe()).Exists(program.Path))
        {
            _logger.LogWarning("Skipping missing media for {Title}", program.Title);
            return (false, -1, null);
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

        var (args, hardwareDecode) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: softwareDecodeOnly || MustDecodeInSoftware(program, subtitle), program.IsHdr, program.DefaultAudioOrdinal, burstSeconds(), durationLimit, subtitleFonts, SilenceFor(program, offset, durationLimit), program.SourceColorSpace, stats);
        var (total, lastOut, exitCode) = await RunFfmpegAsync(ffmpeg, args, program.Title, output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds)).ConfigureAwait(false);

        // The per-item path has no continuous decoder to fall back, so retry a hardware-decode that produced
        // nothing (e.g. a codec the GPU can't decode) once in software, so one bad item can't blank the channel.
        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: true, program.IsHdr, program.DefaultAudioOrdinal, burstSeconds(), durationLimit, subtitleFonts, SilenceFor(program, offset, durationLimit), program.SourceColorSpace, stats);
            (total, lastOut, exitCode) = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds)).ConfigureAwait(false);
        }

        // A burn-in tune-in seek runs with -copyts, under which ffmpeg's -progress position is meaningless (it
        // reads the timeline start); the end used here comes from the output's own packet timestamps instead (see
        // ProducerEndSeconds), which are exact under -copyts too. Chaining such an item from the schedule instead
        // started the next item before this one's last sound had played (the audio runs a frame past the
        // picture), overlapping the two at the seam.

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

        var intended = durationLimit ?? (TimeSpan.FromTicks(program.DurationTicks) - offset);
        return (total > 0, lastOut, total > 0 ? CutShort(exitCode, cancellationToken.IsCancellationRequested, timeline, lastOut, intended) : null);
    }

    // Streams a standby slate (colour bars + silence), looped, for the hold given (or until the client
    // disconnects). Shown when a channel has no playable content so viewers get an intentional standby card, not a
    // black screen. Starts on the channel timeline where the caller left off (after any items that did play), so
    // timestamps never regress. It encodes the channel's configured codecs (in software: a static card costs next
    // to nothing, and it must not depend on the hardware path that may be why content failed), so the copy-mode
    // segmenter's playlist never switches codec when standby takes over. Its progress keeps the session timeline
    // advancing, so whatever follows resumes after the slate rather than over it. Returns the timeline position
    // the slate reached, or null when the slate itself could not be produced.
    private async Task<TimeSpan?> StreamSlateAsync(string ffmpeg, TimeSpan timelineBase, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TimeSpan? hold = null, string? message = null)
    {
        var (width, bitrate, videoCodec, audioCodec) = OutputFor(stats);
        var video = _encoders.ResolveVideo(videoCodec, allowHardware: false);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
        const double clipSeconds = 10.0;

        var font = FontLocator.Find();
        var timeline = timelineBase;
        var clips = 0;
        var holdStarted = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested && (hold is null || DateTime.UtcNow - holdStarted < hold.Value))
        {
            var args = StreamArguments.BuildSlate(width, bitrate, clipSeconds, timeline, font, video, audioEncoder, audioBitrate, message);
            var started = DateTime.UtcNow;

            // Only the first clip's command goes into the session log; the loop repeats it every ten seconds.
            var (total, lastOut, _) = await RunFfmpegAsync(ffmpeg, args, "standby", output, cancellationToken, stats, new SpeedTracker(timeline.TotalSeconds), quiet: clips++ > 0).ConfigureAwait(false);
            if (total == 0)
            {
                return null; // ffmpeg could not even produce the slate; avoid a hot loop.
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

        return timeline;
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

        var scanner = new TsEndScanner();
        var (total, exitCode, stalled, stderr, slowExit) = await PumpProducerAsync(process, stderrTask, label, output, cancellationToken, quiet ? null : stats, scanner).ConfigureAwait(false);
        if (FailureOutcome(total, exitCode, stalled, cancellationToken.IsCancellationRequested, slowExit) is { } outcome)
        {
            var report = _reports.Write(label, args, outcome, stderr, DateTime.UtcNow);
            if (report is not null)
            {
                _logger.LogWarning("Live Channels: ffmpeg for {Label} failed ({Outcome}); details in {Report}", label, outcome, report);
                stats?.AppendLog(label + ": failure report written to " + report);
            }
        }

        // A run that wrote its whole output and was killed only for being slow to exit (a hardware encoder tearing
        // down) ended cleanly: callers see a clean exit, so it is neither "cut short" nor kept from teaching the
        // schedule the item's real length.
        return (total, ProducerEndSeconds(tracker, scanner, stats), slowExit && total > 0 ? 0 : exitCode);
    }

    /// <summary>
    /// Describes a producer run that failed, or returns <c>null</c> for one that did not: a run the session
    /// cancelled (the viewer left, or the schedule moved on) is not a failure, and neither is a clean exit.
    /// </summary>
    /// <param name="total">Bytes the run wrote.</param>
    /// <param name="exitCode">Its exit code: the signal exit status (137 on Linux and macOS) when it had to be killed, -1 when it never reported one.</param>
    /// <param name="stalled">Whether the watchdog killed it as hung.</param>
    /// <param name="cancelled">Whether the session cancelled it.</param>
    /// <param name="slowExitAfterOutput">Whether it finished its output and was killed only for being slow to exit.</param>
    /// <returns>The failure in a sentence, or <c>null</c>.</returns>
    internal static string? FailureOutcome(long total, int exitCode, bool stalled, bool cancelled, bool slowExitAfterOutput = false)
    {
        if (cancelled || (slowExitAfterOutput && total > 0))
        {
            return null;
        }

        if (stalled)
        {
            return "hung: stopped writing output after " + total.ToString(CultureInfo.InvariantCulture) + " bytes and was killed";
        }

        if (total == 0)
        {
            return "produced no output (exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return exitCode != 0
            ? "exited with code " + exitCode.ToString(CultureInfo.InvariantCulture) + " after " + total.ToString(CultureInfo.InvariantCulture) + " bytes"
            : null;
    }

    /// <summary>
    /// Where a finished producer's output ended, as the start of its last frame on the channel timeline (the
    /// value NextTimeline chains the next producer from, one frame on). Read from the packet timestamps that went
    /// through the pump: the next producer then starts after this output's last frame of picture AND of sound,
    /// whatever the encoder's frame-reorder delay. ffmpeg's -progress position, the fallback when no timestamp was
    /// seen, follows the decode timestamps instead and trails the last frame by that delay (two frames for
    /// libx264), which started every next item inside the previous one. The same position is recorded as the
    /// session's timeline, so a producer restarted under a viewer starts there too.
    /// </summary>
    /// <param name="tracker">The producer's progress tracker.</param>
    /// <param name="scanner">The scanner that watched the producer's output.</param>
    /// <param name="stats">The session stats to record the timeline in, or <c>null</c>.</param>
    /// <returns>The start of the output's last frame on the channel timeline, or -1 when unknown.</returns>
    internal static double ProducerEndSeconds(SpeedTracker tracker, TsEndScanner scanner, SessionStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(scanner);
        const double frame = 1.0 / StreamArguments.OutputFps;
        var reference = tracker.LastContentSeconds >= 0 ? tracker.LastContentSeconds : tracker.BaseSeconds;
        if (scanner.NextStartSeconds(frame, reference) is not { } next || next - frame <= tracker.BaseSeconds)
        {
            return tracker.LastContentSeconds;
        }

        // The session records where the last frame starts (a restarted producer resumes one frame after it).
        stats?.ObserveTimeline(next - frame);
        return next - frame;
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
    // Returns the bytes written and the producer's exit code (the kill's signal status when it had to be killed, -1
    // when it never reported one).
    private async Task<(long Total, int ExitCode, bool Stalled, string Stderr, bool SlowExitAfterOutput)> PumpProducerAsync(Process process, Task<string> stderrTask, string label, Stream output, CancellationToken cancellationToken, SessionStats? stats = null, TsEndScanner? scanner = null)
    {
        long total = 0;
        var exitCode = -1;
        var stalled = false;
        var slowExitAfterOutput = false;
        var stderr = string.Empty;
        var buffer = new byte[BufferSize];
        IOException? outputClosed = null;
        try
        {
            var naturalEnd = false;
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                while (true)
                {
                    var timeout = total == 0 ? FirstOutputTimeout : StallTimeout;
                    var read = await ReadOrStallAsync(stdout, buffer, timeout, cancellationToken).ConfigureAwait(false);
                    if (read is null)
                    {
                        stalled = true;
                        // Hung: the finally below kills it, which ends the run like any other failure.
                        _logger.LogWarning("Live Channels: ffmpeg for {Label} wrote nothing for {Seconds:F0}s; killing it as hung", label, timeout.TotalSeconds);
                        stats?.AppendLog(label + ": no output for " + timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s; killed as hung");
                        break;
                    }

                    if (read == 0)
                    {
                        naturalEnd = true;
                        break;
                    }

                    try
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read.Value), cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        outputClosed = ex;
                        break;
                    }

                    scanner?.Feed(buffer.AsSpan(0, read.Value));
                    total += read.Value;
                }

                if (naturalEnd)
                {
                    try
                    {
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        outputClosed = ex;
                    }
                }
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
                // A run that finished its output but had to be killed because it was slow to exit (a hardware
                // encoder tearing down) ended cleanly; its exit code is the kill's, not a failure.
                slowExitAfterOutput = await KillAndWaitAsync(process, graceful: naturalEnd).ConfigureAwait(false) && naturalEnd;
            }

            stderr = await AwaitStderrAsync(stderrTask).ConfigureAwait(false);
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

        // The output (the segmenter) is gone, not the producer: nothing this session runs can be delivered any
        // more, so end it now rather than counting the producer a failure and spawning item after item (each
        // "failing" the same way) until standby, burying the real cause under misleading failure reports. The
        // session's own recovery restarts the whole pipeline if a viewer is still there.
        if (outputClosed is not null && !cancellationToken.IsCancellationRequested)
        {
            stats?.AppendLog(label + ": the segmenter stopped accepting output; ending this producer");
            throw new OutputClosedException("The stream output closed while " + label + " was writing", outputClosed);
        }

        return (total, exitCode, stalled, stderr, slowExitAfterOutput);
    }

    /// <summary>
    /// Reads from a producer's output, giving up if nothing arrives within <paramref name="timeout"/>. A hung
    /// process holds its pipe open without writing, so a plain read would wait forever.
    /// </summary>
    /// <param name="source">The producer's stdout.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="timeout">How long to wait for data.</param>
    /// <param name="cancellationToken">Cancelled when the stream is closed.</param>
    /// <returns>The bytes read (0 at end of output), or <c>null</c> when the producer is hung. The read is then
    /// still pending and completes once the caller kills the process and its pipe closes.</returns>
    internal static async Task<int?> ReadOrStallAsync(Stream source, Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pending = source.ReadAsync(buffer, cancellationToken).AsTask();
        try
        {
            return await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Observe the abandoned read's eventual fault (the pipe closing under it) so it is never reported as
            // an unobserved task exception.
            _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }
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

    private (List<string> Args, bool HardwareDecode) BuildArguments(string path, TimeSpan offset, TimeSpan timeline, (int RelativeIndex, bool IsText)? forcedSubtitle, int sourceHeight, string? externalSubtitlePath, bool softwareDecode, bool isHdr, int? audioOrdinal, double initialBurstSeconds, TimeSpan? durationLimit = null, string? subtitleFontsDir = null, TimeSpan? silentAudio = null, string? colorSpace = null, SessionStats? stats = null)
    {
        var (width, bitrate, videoCodec, audioCodec) = OutputFor(stats);
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
        // Outside the GPU-resident pipeline an HDR item tone-maps in software: with jellyfin-ffmpeg's tonemapx and
        // the server's own settings when this ffmpeg has it, else with the portable zscale chain.
        var toneMap = isHdr && !intelGpu && _encoder.SupportsFilter("tonemapx") ? _encoders.SoftwareToneMap() : null;
        var args = StreamArguments.Build(path, offset, timeline, width, bitrate, video, audioEncoder, audioBitrate, forcedSubtitle, externalSubtitlePath, forceSoftware, isHdr, audioOrdinal, initialBurstSeconds, durationLimit, subtitleStyle, subtitleFontsDir, silentAudio, toneMap, StreamArguments.InputMatrix(colorSpace, sourceHeight));
        return (args, hardwareDecode);
    }

    /// <summary>
    /// Whether an item must decode in software whatever the hardware: a rotated picture (ffmpeg turns it upright
    /// only on frames in system memory; a GPU decode that keeps frames in VRAM plays it sideways), or a picture
    /// subtitle burned over an SD source (a DVD's VOBSUB canvas is the coded frame, so it must be composited
    /// before the picture is scaled to its display shape, which only the software chain does; on the GPU it was
    /// stretched across the padded frame).
    /// </summary>
    /// <param name="program">The item.</param>
    /// <param name="subtitle">The subtitle burned in, or <c>null</c>.</param>
    /// <returns>Whether to decode in software.</returns>
    internal static bool MustDecodeInSoftware(ProgramEntry program, (int RelativeIndex, bool IsText)? subtitle)
        => program.SourceRotation != 0 || (subtitle is { IsText: false } && program.SourceHeight is > 0 and < 720);

    /// <summary>
    /// How much silence a video-only item needs as its audio track for one run: the video it will play from
    /// <paramref name="offset"/> (to its end, or to the run's cap). <c>null</c> for an item with its own audio, and
    /// for one whose length is unknown, which keeps mapping the item's own (absent) audio rather than generating
    /// silence with no end.
    /// </summary>
    /// <param name="program">The item.</param>
    /// <param name="offset">Where in the item this run starts.</param>
    /// <param name="durationLimit">The run's cap on how much it plays, or <c>null</c>.</param>
    /// <returns>The silence length, or <c>null</c>.</returns>
    internal static TimeSpan? SilenceFor(ProgramEntry program, TimeSpan offset, TimeSpan? durationLimit)
    {
        if (!program.IsVideoOnly)
        {
            return null;
        }

        var remaining = TimeSpan.FromTicks(program.DurationTicks) - offset;
        if (durationLimit is { } limit && limit < remaining)
        {
            remaining = limit;
        }

        return remaining > TimeSpan.Zero ? remaining : null;
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
    // resets the line builder. Progress lines only feed the tracker: a dozen key=value lines arrive every half
    // second, so kept in the tail they would push out the one error line that explains a failure within seconds.
    // The tail is trimmed from the front so it never grows past a few kilobytes.
    private static void FlushStderrLine(StringBuilder line, StringBuilder tail, SessionStats? stats, SpeedTracker? tracker)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        tracker?.Observe(text, stats);
        if (IsProgressLine(text))
        {
            return;
        }

        tail.Append(text).Append('\n');
        const int MaxTail = 4096;
        if (tail.Length > MaxTail)
        {
            tail.Remove(0, tail.Length - MaxTail);
        }
    }

    /// <summary>
    /// Whether a stderr line is one of ffmpeg's <c>-progress</c> key=value lines (<c>frame=</c>, <c>out_time_us=</c>,
    /// <c>speed=</c>, <c>progress=</c>, ...) rather than a log message: it starts with a bare lowercase key and an
    /// equals sign. The value may be space-padded (<c>bitrate= 676.5kbits/s</c>); a log message never starts that
    /// way (it opens with a bracketed context, a capital, or a path).
    /// </summary>
    /// <param name="line">One stderr line.</param>
    /// <returns><c>true</c> for a progress line.</returns>
    internal static bool IsProgressLine(string line)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return false;
        }

        for (var i = 0; i < eq; i++)
        {
            var c = line[i];
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
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

        // Where on the channel timeline this producer's output starts.
        public double BaseSeconds => _baseSeconds;

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
    // Returns whether it had to be killed (false when it exited on its own or was already gone).
    private async Task<bool> KillAndWaitAsync(Process process, bool graceful = false)
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
                return false;
            }
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill ffmpeg process");
            return false;
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

        return true;
    }

    /// <summary>Why a channel went to standby.</summary>
    internal enum StandbyReason
    {
        /// <summary>The channel resolved to no items.</summary>
        NothingToPlay,

        /// <summary>None of the channel's files can be found.</summary>
        MediaUnreachable,

        /// <summary>Item after item failed to play.</summary>
        ItemsFailing,

        /// <summary>The continuous encoder keeps dying.</summary>
        EncoderFailing
    }

    // Where on the timeline the schedule stopped, and why: a standby reason, or null when the schedule only needs
    // planning again (a rebuild the running pipeline cannot follow).
    private sealed record StandbyRequest(TimeSpan At, StandbyReason? Reason);
}
