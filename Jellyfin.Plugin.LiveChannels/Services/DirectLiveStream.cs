// The type is not a Stream; it is an ILiveStream, and Jellyfin's own implementations of that interface carry
// the same suffix (ExclusiveLiveStream, SharedHttpStream), so the conventional name wins over CA1711 here.
#pragma warning disable CA1711

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The plugin-owned live stream handed to Jellyfin through <c>ISupportsDirectStreamProvider</c>. Jellyfin's own
/// LiveTvController serves <see cref="GetStream"/> at its internal /LiveTv/LiveStreamFiles/&lt;UniqueId&gt;/stream.ts
/// endpoint (wrapped in its tail-following ProgressiveFileStream), which is where the opened source's path points —
/// the same delivery route every native tuner stream uses, with nothing exposed by the plugin itself. Serving the
/// session as a continuous probe-able MPEG-TS is what lets playback direct-stream: the open-stream probe replaces
/// the interlace flag Jellyfin force-sets on plugin-provided streams, and with no HLS playlist in the path the
/// 10.11.10+ probe container normalisation has nothing to break.
/// </summary>
public sealed class DirectLiveStream : ILiveStream, IDirectStreamProvider
{
    /// <summary>
    /// The reserve a reader keeps between itself and the producer once the session is established, in segments.
    /// See <see cref="HoldBehindFor"/> for what the reserve buys and how it ramps in.
    /// </summary>
    public const int HoldBehind = 4;

    /// <summary>How many seconds of session age each further segment of hold-back waits for once the ramp has begun.</summary>
    public const int HoldRampSeconds = 10;

    /// <summary>
    /// The session age at which the hold-back starts ramping in: the end of the tune-in burst. Before that the
    /// producer may still be building its lead (on a slow box it is barely ahead of the player), and withholding
    /// a segment then can take away exactly the one the player is about to need.
    /// </summary>
    public const double HoldRampStartSeconds = StreamArguments.InitialBurstSeconds;

    /// <summary>
    /// How many segments behind the newest a reader joining a fresh session starts: the whole initial burst plus
    /// the tune-in card ahead of it, so the oldest segment (the card, then the exact position the schedule asked
    /// for at tune-in) is where playback begins.
    /// </summary>
    public const int FreshStartBehind = (((int)StreamArguments.InitialBurstSeconds + (int)IntroService.IntroSeconds + StreamArguments.SegmentSeconds) / StreamArguments.SegmentSeconds) + 1;

    private readonly string _channelName;
    private readonly string _sessionDir;
    private readonly DateTime _sessionStartedUtc;
    private readonly Func<Task> _close;
    private readonly Action? _onData;
    private readonly ILogger _logger;
    private int _readers;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectLiveStream"/> class.
    /// </summary>
    /// <param name="channelName">The channel name, for log context.</param>
    /// <param name="sessionDir">The session directory holding the rolling segments <see cref="GetStream"/> reads.</param>
    /// <param name="sessionStartedUtc">When the session's producer started, deciding whether readers take the whole young-session backlog or join near the live edge.</param>
    /// <param name="buildMediaSource">Builds the opened media source from the generated <see cref="UniqueId"/> (which the internal endpoint path embeds).</param>
    /// <param name="close">Releases this viewer's consumer when Jellyfin closes the stream.</param>
    /// <param name="logger">The logger the endpoint reader lifecycle is reported to.</param>
    /// <param name="onData">Called whenever a reader actually delivers bytes, which is how the session knows a real viewer is still on the other end.</param>
    public DirectLiveStream(string channelName, string sessionDir, DateTime sessionStartedUtc, Func<string, MediaSourceInfo> buildMediaSource, Func<Task> close, ILogger logger, Action? onData = null)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(sessionDir);
        ArgumentNullException.ThrowIfNull(buildMediaSource);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(logger);

        _channelName = channelName;
        _sessionDir = sessionDir;
        _sessionStartedUtc = sessionStartedUtc;
        _close = close;
        _onData = onData;
        _logger = logger;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        MediaSource = buildMediaSource(UniqueId);
        ConsumerCount = 1;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string? OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string? TunerHostId => null;

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Live Channels: {Channel}: Jellyfin closed the live stream (live id {Id})", _channelName, MediaSource.Id);
        return _close();
    }

    /// <summary>
    /// Opens an independent reader over the session's segments. Called once per consumer of the internal
    /// endpoint (the open-stream probe and the delivery ffmpeg each fetch it separately), so a tune-in with
    /// no reader connections at all means the client never requested the stream.
    /// </summary>
    /// <returns>The continuous MPEG-TS stream.</returns>
    public Stream GetStream()
    {
        var reader = Interlocked.Increment(ref _readers);

        // A session still holding its whole initial-burst backlog serves it all: its oldest segment is the exact
        // position the schedule asked for at tune-in, so the burst becomes the player's opening cushion instead
        // of being discarded. Once the session is older than the burst it produced, a reader (an adopting viewer,
        // or a player reconnecting mid-watch) joins near the live edge instead, so nobody is served minutes-old
        // content just because the rolling window still has it. The edge start stays one segment ahead of the
        // hold-back so a joining reader always has something servable immediately.
        var fresh = DateTime.UtcNow - _sessionStartedUtc < TimeSpan.FromSeconds(StreamArguments.InitialBurstSeconds + 60);
        var startBehind = fresh ? FreshStartBehind : HoldBehind + 1;

        _logger.LogInformation("Live Channels: {Channel}: endpoint reader {Reader} connected (live id {Id})", _channelName, reader, MediaSource.Id);
        return new SegmentConcatStream(
            _sessionDir,
            message => _logger.LogInformation("Live Channels: {Channel}: endpoint reader {Reader} {Message}", _channelName, reader, message),
            startBehind,
            () => HoldBehindFor(DateTime.UtcNow - _sessionStartedUtc),
            _onData);
    }

    /// <summary>
    /// How many of the newest segments a reader withholds at a given session age. Live HLS players sync a fixed
    /// few segments behind whatever edge Jellyfin's delivery remux exposes, regardless of how much backlog sits
    /// earlier in the playlist, so if the remux is fed right up to the producer's newest segment every viewer
    /// permanently rides the encoder's heels and any inter-item spawn gap or encode dip longer than the player's
    /// own small buffer surfaces as a stall. Withholding the newest segments keeps a standing reserve between
    /// producer and delivery: producer gaps shorter than the reserve are absorbed before any player can notice.
    /// The reserve is NOT applied at tune-in, though: a brand-new session has only the segments it has just
    /// burst, and holding those back would make the viewer wait for the reserve to be encoded on top of the
    /// content they are about to watch. Nothing is withheld until <see cref="HoldRampStartSeconds"/>, then the
    /// reserve ramps in one segment per <see cref="HoldRampSeconds"/>, so no single step ever withholds more
    /// than the player has buffered by then.
    /// </summary>
    /// <param name="sessionAge">How long the session's producer has been running.</param>
    /// <returns>The number of newest segments to withhold, from zero up to <see cref="HoldBehind"/>.</returns>
    public static int HoldBehindFor(TimeSpan sessionAge)
    {
        var past = sessionAge.TotalSeconds - HoldRampStartSeconds;
        return past < 0 ? 0 : (int)Math.Clamp(1 + Math.Floor(past / HoldRampSeconds), 1, HoldBehind);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned here: readers are disposed by Jellyfin's response, the session by CloseLiveStream.
    }
}
