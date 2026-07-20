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
    // A session younger than this still holds its whole initial-burst backlog, and its oldest segment is the
    // exact position the schedule asked for at tune-in: readers take everything, so the burst becomes the
    // player's opening cushion instead of being discarded. Comfortably past the producer's 30s initial burst
    // plus the probe and handover latency in front of the first delivery reader.
    private static readonly TimeSpan FreshSessionWindow = TimeSpan.FromSeconds(90);

    // On a fresh session: how far behind the newest segment a reader may start (clamped to the window, so in
    // practice this means "take the whole backlog"). 8 segments x 4s covers the full initial burst.
    private const int FreshStartBehind = 8;

    // On an established session (an adopting viewer, or a player reconnecting mid-watch): join near the live
    // edge instead, so nobody is served minutes-old content just because the rolling window still has it. Kept
    // one segment ahead of the hold-back so a joining reader always has something servable immediately.
    private const int EdgeStartBehind = 4;

    // How many of the newest segments every reader withholds. Live HLS players sync a fixed ~3 segments behind
    // whatever edge Jellyfin's delivery remux exposes — regardless of how much backlog sits earlier in the
    // playlist — so if the remux is fed right up to the producer's newest segment, every viewer permanently
    // rides the encoder's heels and any inter-item spawn gap or encode dip longer than the player's own small
    // buffer surfaces as a stall. Withholding the newest segments keeps a standing reserve between producer and
    // delivery: producer gaps shorter than the reserve are absorbed before any player can notice.
    private const int HoldBehind = 3;

    private readonly string _channelName;
    private readonly string _sessionDir;
    private readonly DateTime _sessionStartedUtc;
    private readonly Func<Task> _close;
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
    public DirectLiveStream(string channelName, string sessionDir, DateTime sessionStartedUtc, Func<string, MediaSourceInfo> buildMediaSource, Func<Task> close, ILogger logger)
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
        var startBehind = DateTime.UtcNow - _sessionStartedUtc < FreshSessionWindow ? FreshStartBehind : EdgeStartBehind;
        _logger.LogInformation("Live Channels: {Channel}: endpoint reader {Reader} connected (live id {Id})", _channelName, reader, MediaSource.Id);
        return new SegmentConcatStream(
            _sessionDir,
            message => _logger.LogInformation("Live Channels: {Channel}: endpoint reader {Reader} {Message}", _channelName, reader, message),
            startBehind,
            HoldBehind);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned here: readers are disposed by Jellyfin's response, the session by CloseLiveStream.
    }
}
