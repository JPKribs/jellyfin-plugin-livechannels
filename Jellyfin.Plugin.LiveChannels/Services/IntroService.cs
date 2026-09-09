using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The tune-in card: a short pre-rendered clip (the channel's logo over an abstract animated backdrop, silent
/// audio) that every fresh session plays first, so the viewer gets a picture the moment the tune-in completes while the real content
/// is still being seeked into and encoded behind it. It is rendered once per channel in the channel's exact
/// output format and cached; at tune-in its bytes are written straight into the segmenter as if it were the
/// first item, and the first real producer chains its timeline from where the card ended, exactly the way
/// every later item chains from the one before it. Nothing about the card is special to the delivery: it is
/// just eight seconds of ordinary channel output that happen to exist before the encoder has produced any.
/// </summary>
/// <remarks>
/// The card is produced in two passes so its elementary streams come out of the SAME encoder and arguments as
/// every real item: a software pass renders the visual to a plain intermediate video, then that video is run
/// through <see cref="StreamArguments.Build"/> like any library file. An intro encoded any other way would
/// hand the stream its first SPS/PPS from a different encoder, and the seam into real content would be a
/// parameter-set switch instead of a continuation.
/// </remarks>
public sealed class IntroService : IDisposable
{
    /// <summary>
    /// The length of the card in seconds. Jellyfin's delivery remux refuses to serve its playlist until it has
    /// cut three segments, and it cuts them on the encoder's keyframes: a card that spans three segments plus
    /// one more keyframe lets all three close from the card alone, so the moment playback can start no longer
    /// depends on how quickly the real content is seeked into and encoded behind it. Half a second past the
    /// third segment is enough for that fourth keyframe (one lands exactly at the mark) plus the fade-out.
    /// </summary>
    public const double IntroSeconds = (3 * StreamArguments.SegmentSeconds) + 0.5;

    // Bump whenever the card's rendering or the sidecar's shape changes, so stale caches regenerate.
    private const int Revision = 5;

    // The look: the logo sits top-leading inside a thin bordered frame with an inner gutter, over a slowly
    // drifting dark gradient on which two pairs of coloured lines draw in from the left and keep flowing.
    // Each line is a sine wave travelling at a fixed speed, which is the same thing as a fixed shape scrolling
    // sideways, so it is drawn ONCE as a static strip one period wider than the frame (the per-pixel expression
    // filter that draws it is the only expensive stage, and this way it runs for a single frame) and animated by
    // cropping a moving window out of the strip. That keeps every line pixel-sharp at the channel's native
    // resolution, 4K included, for the cost of a crop per frame.
    private const double LogoFadeInAt = 0.2;
    private const double LogoFadeInSeconds = 0.8;
    private const double FadeOutSeconds = 0.6;
    private const double LineDrawInSeconds = 2.4;
    private const double LayerBDrawInDelay = 0.36;
    private const int DrawInEdgePixels = 60;

    /// <summary>The number of lines the card draws.</summary>
    internal static int LineCount => Lines.Length;

    /// <summary>The soft edge width of the draw-in reveal, in pixels.</summary>
    internal static int DrawInEdge => DrawInEdgePixels;
    private const string CaptionText = "Starting Channel";
    private const string LineColorA = "#4fd8ff";
    private const string LineColorB = "#c66bff";

    // The four lines: vertical baseline and swing as fractions of the frame height, how many wavelengths span
    // the frame, angular speed in radians per second, phase, and travel direction. The first two make layer A,
    // the last two layer B.
    private static readonly LineSpec[] Lines =
    [
        new(0.62, 0.07, 1.1, 0.9, 0.0, 1),
        new(0.70, 0.09, 0.8, 0.7, 1.3, -1),
        new(0.78, 0.06, 1.4, 1.1, 2.1, 1),
        new(0.55, 0.05, 0.6, 0.5, 0.7, -1),
    ];
    private const string BackdropColors = "c0=#0b1020:c1=#1a0f33:c2=#0a1c2e:c3=#12082a:nb_colors=4:speed=0.012";

    private readonly IMediaEncoder _encoder;
    private readonly EncoderResolver _encoders;
    private readonly string _root;
    private readonly ILogger<IntroService> _logger;

    // One render at a time: a guide refresh asks for every channel's card in one sweep, and thirty concurrent
    // ffmpegs would starve any live session running alongside.
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly ConcurrentDictionary<int, Task> _inFlight = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg and ffprobe.</param>
    /// <param name="encoders">The encoder resolver, so the card is encoded with the channel's real encoder.</param>
    /// <param name="appPaths">Application paths; cards live under the plugin's asset cache.</param>
    /// <param name="logger">The logger.</param>
    public IntroService(IMediaEncoder encoder, EncoderResolver encoders, IApplicationPaths appPaths, ILogger<IntroService> logger)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _encoder = encoder;
        _encoders = encoders;
        _logger = logger;
        _root = Path.Combine(appPaths.CachePath, "livechannels-assets", "intros");
    }

    /// <summary>
    /// Returns the channel's card if one is cached for the CURRENT output profile, else null. Never renders: a
    /// tune-in must not wait for a render, so a missing or stale card simply means this session plays without
    /// one (and <see cref="EnsureAsync"/> is what makes the next one have it).
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The cached card, or null.</returns>
    public CachedIntro? TryGetCached(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            var sidecar = ReadSidecar(channel.Number);
            var ts = TsPath(channel.Number);
            if (sidecar is null || sidecar.Revision != Revision || !string.Equals(sidecar.Profile, CurrentProfile().Key, StringComparison.Ordinal) || !File.Exists(ts))
            {
                return null;
            }

            return new CachedIntro(ts, TimeSpan.FromSeconds(sidecar.DurationSeconds), sidecar.EndSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Live Channels: could not read the intro cache for channel {Number}", channel.Number);
            return null;
        }
    }

    /// <summary>
    /// Makes sure the channel's card exists for the current output profile and logo, rendering it in the
    /// background when it is missing or stale. Renders are single-flight per channel and serialised across
    /// channels; the returned task completes when this channel's card is up to date, but callers on a hot path
    /// should not await it.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="logoPath">The channel's logo file (uploaded or generated), or null for a plain black card.</param>
    /// <returns>A task that completes when the card is current; it never faults.</returns>
    public Task EnsureAsync(Channel channel, string? logoPath)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrEmpty(_encoder.EncoderPath))
        {
            return Task.CompletedTask;
        }

        var profile = CurrentProfile();
        var logoStamp = LogoStamp(logoPath);
        try
        {
            var sidecar = ReadSidecar(channel.Number);
            if (sidecar is not null
                && sidecar.Revision == Revision
                && string.Equals(sidecar.Profile, profile.Key, StringComparison.Ordinal)
                && string.Equals(sidecar.LogoStamp, logoStamp, StringComparison.Ordinal)
                && File.Exists(TsPath(channel.Number)))
            {
                return Task.CompletedTask;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Live Channels: intro cache for channel {Number} is unreadable; re-rendering", channel.Number);
        }

        return _inFlight.GetOrAdd(channel.Number, _ => RenderAndForgetAsync(channel, logoPath, logoStamp, profile));
    }

    private async Task RenderAndForgetAsync(Channel channel, string? logoPath, string logoStamp, (string Key, int Width, int Bitrate, VideoEncoderProfile Video, string AudioEncoder, int AudioBitrate) profile)
    {
        try
        {
            await _renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RenderAsync(channel, logoPath, logoStamp, profile).ConfigureAwait(false);
            }
            finally
            {
                _renderGate.Release();
            }
        }
        catch (Exception ex)
        {
            // A card is a nicety: a failed render must never surface anywhere but the log.
            _logger.LogWarning(ex, "Live Channels: could not render the tune-in card for {Name}", channel.Name);
        }
        finally
        {
            _inFlight.TryRemove(channel.Number, out _);
        }
    }

    private async Task RenderAsync(Channel channel, string? logoPath, string logoStamp, (string Key, int Width, int Bitrate, VideoEncoderProfile Video, string AudioEncoder, int AudioBitrate) profile)
    {
        var ffmpeg = _encoder.EncoderPath;
        Directory.CreateDirectory(_root);
        var stopwatch = Stopwatch.StartNew();
        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var prefix = Path.Combine(_root, channel.Number.ToString(CultureInfo.InvariantCulture) + "-" + token);
        var source = prefix + ".src.mp4";
        var rendered = prefix + ".tmp.ts";
        var (width, height) = FrameSize(profile.Width);
        var strips = new LineStrips(prefix, width, height);
        try
        {
            // Pass one: the static line strips and the draw-in mask, one frame each at native resolution.
            var (exit, error) = await RunAsync(ffmpeg, BuildStripArguments(strips)).ConfigureAwait(false);
            if (exit != 0 || !strips.AllExist())
            {
                _logger.LogWarning("Live Channels: tune-in card line render failed for {Name} (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                return;
            }

            // Pass two: the visual, as an ordinary video file at the channel's frame size.
            var sourceArgs = BuildSourceArguments(logoPath, strips, source);
            (exit, error) = await RunAsync(ffmpeg, sourceArgs).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(source) || new FileInfo(source).Length == 0)
            {
                _logger.LogWarning("Live Channels: tune-in card source render failed for {Name} (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                return;
            }

            // Pass three: that file through the channel's real encoder and arguments, output to a file instead
            // of the segmenter. A burst longer than the card reads it flat out instead of at realtime.
            var args = StreamArguments.Build(source, TimeSpan.Zero, TimeSpan.Zero, profile.Width, profile.Bitrate, profile.Video, profile.AudioEncoder, profile.AudioBitrate, null, initialBurstSeconds: IntroSeconds * 10);
            args[args.Count - 1] = rendered;
            args.Insert(0, "-y");
            (exit, error) = await RunAsync(ffmpeg, args).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(rendered) || new FileInfo(rendered).Length == 0)
            {
                _logger.LogWarning("Live Channels: tune-in card encode failed for {Name} with {Encoder} (exit {Exit}): {Error}", channel.Name, profile.Video.Name, exit, error.Trim());
                return;
            }

            // The true end of the card (latest packet end over both streams) is what the first real producer
            // chains its timeline from; chaining from the nominal length instead overlaps the last audio frame.
            var end = await ProbeEndSecondsAsync(rendered).ConfigureAwait(false) ?? (IntroSeconds + 0.1);

            var final = TsPath(channel.Number);
            File.Move(rendered, final, overwrite: true);
            var sidecar = new Sidecar(Revision, profile.Key, logoStamp, IntroSeconds, end);
            var sidecarTmp = SidecarPath(channel.Number) + ".tmp";
            await File.WriteAllTextAsync(sidecarTmp, JsonSerializer.Serialize(sidecar)).ConfigureAwait(false);
            File.Move(sidecarTmp, SidecarPath(channel.Number), overwrite: true);

            _logger.LogInformation(
                "Live Channels: rendered the tune-in card for {Name} ({Encoder}, ends at {End:F3}s) in {Seconds:F1}s",
                channel.Name,
                profile.Video.Name,
                end,
                stopwatch.Elapsed.TotalSeconds);
        }
        finally
        {
            TryDelete(source);
            TryDelete(rendered);
            foreach (var file in strips.Files)
            {
                TryDelete(file);
            }
        }
    }

    // Draws each line's strip and the draw-in mask, a single frame each. A strip is the line's profile (a thin,
    // sharp core with a faint wider halo, both scaled with the frame so the look holds at any resolution) over
    // exactly one period plus the frame width, so any window one frame wide taken from it is one phase of the
    // wave and the window can slide forever without a seam. The mask is a soft left-to-right edge two frames wide
    // that the reveal slides across.
    internal static List<string> BuildStripArguments(LineStrips strips)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var h = strips.Height.ToString(CultureInfo.InvariantCulture);
        var scale = strips.Width / 1280.0;
        var core2 = (2 * Math.Pow(1.1 * scale, 2)).ToString("0.###", CultureInfo.InvariantCulture);
        var halo2 = (2 * Math.Pow(5.0 * scale, 2)).ToString("0.###", CultureInfo.InvariantCulture);
        for (var i = 0; i < Lines.Length; i++)
        {
            var line = Lines[i];
            var period = strips.Periods[i];
            var centre = "(" + F(line.Baseline) + "*H+" + F(line.Amplitude) + "*H*sin(" + F(line.Direction) + "*6.28318530718*X/" + period.ToString(CultureInfo.InvariantCulture) + "+" + F(line.Phase) + "))";
            var expression = "min(255,255*(exp(-pow(Y-" + centre + ",2)/" + core2 + ")+0.35*exp(-pow(Y-" + centre + ",2)/" + halo2 + ")))";
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + (strips.Width + period).ToString(CultureInfo.InvariantCulture) + "x" + h + ":r=1:d=1,format=gray,geq=lum='" + expression + "'"]);
        }

        var reveal = strips.Width + DrawInEdgePixels;
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + strips.MaskWidth.ToString(CultureInfo.InvariantCulture) + "x" + h + ":r=1:d=1,format=gray,geq=lum='255*clip((" + reveal.ToString(CultureInfo.InvariantCulture) + "-X)/" + DrawInEdgePixels.ToString(CultureInfo.InvariantCulture) + ",0,1)'"]);

        for (var i = 0; i < Lines.Length; i++)
        {
            args.AddRange(["-map", i.ToString(CultureInfo.InvariantCulture), "-frames:v", "1", strips.Strips[i]]);
        }

        args.AddRange(["-map", Lines.Length.ToString(CultureInfo.InvariantCulture), "-frames:v", "1", strips.Mask]);
        return args;
    }

    // Renders the card's visual as a plain 8-bit progressive video at the channel's frame size and rate with a
    // silent stereo track. Any codec would do here since the next pass re-encodes it; x264 is universally
    // present. Inputs: the logo (when present), the gradient backdrop, the four strips, the mask, and silence.
    internal static List<string> BuildSourceArguments(string? logoPath, LineStrips strips, string output)
        => BuildSourceArguments(logoPath, strips, output, FontLocator.Find());

    internal static List<string> BuildSourceArguments(string? logoPath, LineStrips strips, string output, string? fontPath)
    {
        var width = strips.Width;
        var height = strips.Height;
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        var fps = StreamArguments.OutputFps.ToString(CultureInfo.InvariantCulture);
        var size = w + "x" + h;
        var seconds = IntroSeconds.ToString(CultureInfo.InvariantCulture);
        var fadeOutAt = (IntroSeconds - FadeOutSeconds).ToString(CultureInfo.InvariantCulture);

        var hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var next = 0;
        var logo = -1;
        if (hasLogo)
        {
            args.AddRange(["-loop", "1", "-framerate", fps, "-i", logoPath!]);
            logo = next++;
        }

        args.AddRange(["-f", "lavfi", "-i", "gradients=s=" + size + ":" + BackdropColors + ":r=" + fps]);
        var backdrop = next++;
        var stripInputs = new int[Lines.Length];
        for (var i = 0; i < Lines.Length; i++)
        {
            args.AddRange(["-loop", "1", "-framerate", fps, "-i", strips.Strips[i]]);
            stripInputs[i] = next++;
        }

        args.AddRange(["-loop", "1", "-framerate", fps, "-i", strips.Mask]);
        var mask = next++;
        args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        var audio = next++;

        // Each strip scrolls at its line's speed; the two lines of a layer screen together; the layer is then
        // gated by the sliding reveal (layer B a beat behind layer A), coloured through its own alpha, and laid
        // over the backdrop.
        var filter = string.Empty;
        for (var i = 0; i < Lines.Length; i++)
        {
            filter += "[" + stripInputs[i].ToString(CultureInfo.InvariantCulture) + ":v]format=gray,crop=w=" + w + ":h=" + h + ":x='mod(" + strips.PixelsPerSecond[i].ToString("0.####", CultureInfo.InvariantCulture) + "*t," + strips.Periods[i].ToString(CultureInfo.InvariantCulture) + ")':y=0[s" + i.ToString(CultureInfo.InvariantCulture) + "];";
        }

        var reveal = (width + DrawInEdgePixels).ToString(CultureInfo.InvariantCulture);
        var drawIn = LineDrawInSeconds.ToString(CultureInfo.InvariantCulture);
        var delay = LayerBDrawInDelay.ToString(CultureInfo.InvariantCulture);
        filter += "[" + mask.ToString(CultureInfo.InvariantCulture) + ":v]format=gray,split[m0][m1];"
            + "[m0]crop=w=" + w + ":h=" + h + ":x='min(" + reveal + ",max(0," + reveal + "*(1-t/" + drawIn + ")))':y=0[mA];"
            + "[m1]crop=w=" + w + ":h=" + h + ":x='min(" + reveal + ",max(0," + reveal + "*(1-(t-" + delay + ")/" + drawIn + ")))':y=0[mB];"
            + "[s0][s1]blend=all_mode=screen[a0];[a0][mA]blend=all_mode=multiply[laf];"
            + "[s2][s3]blend=all_mode=screen[b0];[b0][mB]blend=all_mode=multiply[lbf];"
            + "color=c=" + LineColorA + ":s=" + size + ":r=" + fps + "[ca];"
            + "color=c=" + LineColorB + ":s=" + size + ":r=" + fps + "[cb];"
            + "[ca][laf]alphamerge[lineA];[cb][lbf]alphamerge[lineB];"
            + "[" + backdrop.ToString(CultureInfo.InvariantCulture) + ":v][lineA]overlay=format=auto[bg1];[bg1][lineB]overlay=format=auto[bg2];";

        if (hasLogo)
        {
            // Logo at 16% of the frame height, an inner gutter, a thin translucent border, a margin of 5% of the
            // height from the frame's top-left corner, fading in. The caption sits on the same line, vertically
            // centred on the logo's frame: the logo layer is widened to the right by a text budget so the caption
            // is drawn onto it (its x measured back from that budget, which is how the logo's own width, unknown
            // until ffmpeg scales it, is found) and the two fade in as one.
            var pad = height * 5 / 100;
            var logoHeight = height * 16 / 100;
            const int Gutter = 14;
            const int Border = 2;
            var caption = string.Empty;
            if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
            {
                var budget = width / 2;
                var gap = height * 3 / 100;
                var fontSize = logoHeight * 38 / 100;
                caption = "pad=iw+" + budget.ToString(CultureInfo.InvariantCulture) + ":ih:0:0:color=#00000000,"
                    + "drawtext=fontfile='" + EscapeFilterPath(fontPath) + "':text='" + CaptionText + "':fontcolor=white@0.9:fontsize=" + fontSize.ToString(CultureInfo.InvariantCulture)
                    + ":x=w-" + budget.ToString(CultureInfo.InvariantCulture) + "+" + gap.ToString(CultureInfo.InvariantCulture) + ":y=(h-th)/2-(descent/2),";
            }

            filter += "[" + logo.ToString(CultureInfo.InvariantCulture) + ":v]format=rgba,scale=-2:" + logoHeight.ToString(CultureInfo.InvariantCulture) + ":flags=lanczos,"
                + "pad=iw+" + (2 * Gutter).ToString(CultureInfo.InvariantCulture) + ":ih+" + (2 * Gutter).ToString(CultureInfo.InvariantCulture) + ":" + Gutter.ToString(CultureInfo.InvariantCulture) + ":" + Gutter.ToString(CultureInfo.InvariantCulture) + ":color=#00000000,"
                + "pad=iw+" + (2 * Border).ToString(CultureInfo.InvariantCulture) + ":ih+" + (2 * Border).ToString(CultureInfo.InvariantCulture) + ":" + Border.ToString(CultureInfo.InvariantCulture) + ":" + Border.ToString(CultureInfo.InvariantCulture) + ":color=white@0.35,"
                + caption
                + "fade=t=in:st=" + LogoFadeInAt.ToString(CultureInfo.InvariantCulture) + ":d=" + LogoFadeInSeconds.ToString(CultureInfo.InvariantCulture) + ":alpha=1[logo];"
                + "[bg2][logo]overlay=x=" + (pad - Gutter - Border).ToString(CultureInfo.InvariantCulture) + ":y=" + (pad - Gutter - Border).ToString(CultureInfo.InvariantCulture) + ":format=auto[bg3];";
        }
        else
        {
            filter += "[bg2]null[bg3];";
        }

        // A whisper of temporal noise keeps the channel encoder from banding the dark gradient into blotches.
        filter += "[bg3]noise=alls=3:allf=t+u,fade=t=out:st=" + fadeOutAt + ":d=" + FadeOutSeconds.ToString(CultureInfo.InvariantCulture) + ",format=yuv420p[v]";

        args.AddRange(["-filter_complex", filter, "-map", "[v]", "-map", audio.ToString(CultureInfo.InvariantCulture) + ":a"]);
        args.AddRange([
            "-t", seconds,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", "-g", (StreamArguments.OutputFps * StreamArguments.SegmentSeconds).ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
            output
        ]);
        return args;
    }

    /// <summary>A line's spatial frequency and angular speed, for the strip geometry.</summary>
    /// <param name="index">The line index.</param>
    /// <returns>Wavelengths per frame width, and radians per second.</returns>
    internal static (double Frequency, double Speed) LineMotion(int index) => (Lines[index].Frequency, Lines[index].Speed);

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // A path inside a filter option: backslashes, quotes and colons all need escaping (Windows drive letters).
    private static string EscapeFilterPath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

    private async Task<double?> ProbeEndSecondsAsync(string file)
    {
        var ffprobe = _encoder.ProbePath;
        if (string.IsNullOrEmpty(ffprobe))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", file })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await stderr.ConfigureAwait(false);

        double max = -1;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            {
                max = Math.Max(max, pts + duration);
            }
        }

        return max > 0 ? max : null;
    }

    private static async Task<(int Exit, string Error)> RunAsync(string ffmpeg, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await stderr.ConfigureAwait(false));
    }

    // The channel output profile the card must match, keyed so any change (size, bitrate, codec, the concrete
    // encoder or its arguments) invalidates the cache.
    private (string Key, int Width, int Bitrate, VideoEncoderProfile Video, string AudioEncoder, int AudioBitrate) CurrentProfile()
    {
        var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, VideoCodec.H264, AudioCodec.Aac);
        var video = _encoders.ResolveVideo(videoCodec, allowHardware: true);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
        var key = Hash(string.Join(
            '|',
            "i" + Revision.ToString(CultureInfo.InvariantCulture),
            width.ToString(CultureInfo.InvariantCulture),
            bitrate.ToString(CultureInfo.InvariantCulture),
            ((int)videoCodec).ToString(CultureInfo.InvariantCulture),
            ((int)audioCodec).ToString(CultureInfo.InvariantCulture),
            video.Name,
            video.PixelStage,
            string.Join(' ', video.ExtraEncoderArgs),
            string.Join(' ', video.InitArgs)));
        return (key, width, bitrate, video, audioEncoder, audioBitrate);
    }

    private static (int Width, int Height) FrameSize(int width)
    {
        var height = (int)Math.Round(width * 9.0 / 16.0);
        if (height % 2 != 0)
        {
            height++;
        }

        return (width, height);
    }

    private static string LogoStamp(string? logoPath)
    {
        if (string.IsNullOrEmpty(logoPath))
        {
            return "none";
        }

        try
        {
            var info = new FileInfo(logoPath);
            return info.Exists
                ? string.Join('|', logoPath, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), info.Length.ToString(CultureInfo.InvariantCulture))
                : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "none";
        }
    }

    private string TsPath(int number) => Path.Combine(_root, number.ToString(CultureInfo.InvariantCulture) + ".ts");

    private string SidecarPath(int number) => Path.Combine(_root, number.ToString(CultureInfo.InvariantCulture) + ".json");

    private Sidecar? ReadSidecar(int number)
    {
        var path = SidecarPath(number);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not delete {Path}", path);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _renderGate.Dispose();

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

    // What the cache knows about a rendered card: which output profile and logo it was rendered for (so it can be
    // judged stale), and where its timeline really ends.
    private sealed record Sidecar(int Revision, string Profile, string LogoStamp, double DurationSeconds, double EndSeconds);
}

/// <summary>
/// A cached tune-in card ready to be played.
/// </summary>
/// <param name="Path">The MPEG-TS file holding the card.</param>
/// <param name="Duration">The card's nominal length.</param>
/// <param name="EndSeconds">The card's true end on its own timeline (the latest packet end across both streams), which the first real producer chains from.</param>
public sealed record CachedIntro(string Path, TimeSpan Duration, double EndSeconds);

/// <summary>One of the card's lines: a sine wave described relative to the frame.</summary>
/// <param name="Baseline">Vertical centre as a fraction of the frame height.</param>
/// <param name="Amplitude">Swing as a fraction of the frame height.</param>
/// <param name="Frequency">How many wavelengths span the frame width.</param>
/// <param name="Speed">Angular speed in radians per second.</param>
/// <param name="Phase">Phase offset in radians.</param>
/// <param name="Direction">Travel direction, 1 or -1.</param>
internal sealed record LineSpec(double Baseline, double Amplitude, double Frequency, double Speed, double Phase, int Direction);

/// <summary>
/// The static strips a card's lines are cut from, and where they live on disk during a render.
/// </summary>
internal sealed class LineStrips
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LineStrips"/> class.
    /// </summary>
    /// <param name="prefix">Path prefix (directory plus a per-render token) the strip files are named under.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    public LineStrips(string prefix, int width, int height)
    {
        Width = width;
        Height = height;
        var lines = IntroService.LineCount;
        Strips = new string[lines];
        Periods = new int[lines];
        PixelsPerSecond = new double[lines];
        for (var i = 0; i < lines; i++)
        {
            Strips[i] = prefix + ".line" + i.ToString(CultureInfo.InvariantCulture) + ".png";
            var (frequency, speed) = IntroService.LineMotion(i);

            // One period in whole pixels (so the strip tiles seamlessly), and the scroll speed that reproduces the
            // wave's angular speed: a phase advance of w radians per second is w/(2pi) periods per second.
            Periods[i] = Math.Max(2, (int)Math.Round(width / frequency));
            PixelsPerSecond[i] = Periods[i] * speed / (2 * Math.PI);
        }

        Mask = prefix + ".mask.png";
        MaskWidth = (2 * width) + IntroService.DrawInEdge;
    }

    /// <summary>Gets the frame width.</summary>
    public int Width { get; }

    /// <summary>Gets the frame height.</summary>
    public int Height { get; }

    /// <summary>Gets the strip file per line.</summary>
    public string[] Strips { get; }

    /// <summary>Gets each line's period in pixels.</summary>
    public int[] Periods { get; }

    /// <summary>Gets each line's scroll speed in pixels per second.</summary>
    public double[] PixelsPerSecond { get; }

    /// <summary>Gets the draw-in mask file.</summary>
    public string Mask { get; }

    /// <summary>Gets the mask strip's width: two frames plus the soft edge.</summary>
    public int MaskWidth { get; }

    /// <summary>Gets every file a render writes.</summary>
    public IEnumerable<string> Files => Strips.Concat([Mask]);

    /// <summary>Whether every strip and the mask exist on disk.</summary>
    /// <returns>True when all were rendered.</returns>
    public bool AllExist() => Files.All(File.Exists);
}
