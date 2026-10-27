using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The Up Next bumper: 15 seconds before each item showing its artwork, title, episode, details, review score and
/// parental rating over the tune-in card's look, with a countdown and the soft chord. A bumper is part of its item's
/// slot on the schedule, so it never pushes the item later. Bumpers are rendered only while someone watches: when
/// an item starts playing, the next item's bumper renders in the background, and plays when the item ends if it is
/// ready by then (otherwise the slot shows black until the next item starts on time). Everything rendered is cached
/// in the assets folder of the plugin's stream root, and what has not been used for a few days is pruned.
/// </summary>
/// <remarks>
/// A bumper is rendered to a plain intermediate video, not to the channel's output format: the stream plays it
/// through the channel's own encoder exactly like a library item, so it splices into the stream the way every item
/// does and one render serves every output setting. A channel's costly part (backdrop, lines, header and chord) is
/// rendered once per channel and frame size and shared by all of its bumpers.
/// </remarks>
public sealed class BumperService : IDisposable
{
    /// <summary>The length of an Up Next bumper, and of the lead it adds to each slot.</summary>
    public static readonly TimeSpan BumperLength = TimeSpan.FromSeconds(BumperArguments.Seconds);

    // Bump whenever a bumper's rendering changes, so cached bumpers regenerate.
    private const int Revision = 1;

    // The black filler runs a little past a bumper's length, so it can always cover a whole slot lead.
    private const double BlackSeconds = BumperArguments.Seconds + 1;

    // A pass renders 15 seconds of video at most; anything longer is hung, not slow.
    private static readonly TimeSpan RenderPassTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // Bumpers unused for this long are deleted; the sweep runs at most this often.
    private static readonly TimeSpan KeepUnused = TimeSpan.FromDays(3);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IMediaEncoder _encoder;
    private readonly string _root;
    private readonly string _fontsDir;
    private readonly ILogger<BumperService> _logger;

    // One render at a time, so a burst of channels starting together cannot starve the live encodes; the black
    // filler has its own gate, since a stream may be waiting on it.
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly SemaphoreSlim _blackGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private readonly object _fontsLock = new();
    private BumperFonts? _fonts;
    private long _nextPruneTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="BumperService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg and ffprobe.</param>
    /// <param name="appPaths">Application paths; bumpers live in the assets folder of the plugin's stream root.</param>
    /// <param name="logger">The logger.</param>
    public BumperService(IMediaEncoder encoder, IApplicationPaths appPaths, ILogger<BumperService> logger)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _encoder = encoder;
        _logger = logger;
        var assets = PluginPaths.AssetsDir(appPaths);
        _root = Path.Combine(assets, "bumpers");
        _fontsDir = Path.Combine(assets, "fonts");
    }

    /// <summary>
    /// The item's rendered bumper for this channel and frame width, or <c>null</c> when it is not rendered (yet).
    /// Never renders: the stream must not wait for a render.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="program">The item.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <returns>The bumper's video, or <c>null</c>.</returns>
    public string? TryGetCached(Channel channel, ProgramEntry program, int width, string? logoPath)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(program);
        var path = ItemPath(channel, program, width, logoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        Touch(path);
        return path;
    }

    /// <summary>
    /// Starts rendering the item's bumper in the background when it is not cached already. Renders are
    /// single-flight per bumper and run one at a time; this never blocks and never throws.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="program">The item.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    public void Prepare(Channel channel, ProgramEntry program, int width, string? logoPath)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(program);
        if (string.IsNullOrEmpty(_encoder.EncoderPath))
        {
            return;
        }

        MaybePrune();
        var path = ItemPath(channel, program, width, logoPath);
        if (File.Exists(path))
        {
            return;
        }

        // A render that completes synchronously runs its own cleanup before GetOrAdd stores it; drop it here then,
        // or it would sit in the map and block every later render of this bumper.
        var task = _inFlight.GetOrAdd(path, _ => RenderAndForgetAsync(channel, program, width, logoPath, path));
        if (task.IsCompleted)
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task>(path, task));
        }
    }

    /// <summary>
    /// The black filler for a frame width, rendering it first when it is missing (a second or so of work, done
    /// once per width).
    /// </summary>
    /// <param name="width">The channel's output width.</param>
    /// <param name="cancellationToken">Cancelled when the stream closes.</param>
    /// <returns>The filler's video, or <c>null</c> when it could not be rendered.</returns>
    public async Task<string?> EnsureBlackAsync(int width, CancellationToken cancellationToken)
    {
        var (w, h) = IntroService.FrameSize(width);
        var path = Path.Combine(_root, "black-" + w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture) + "-r" + Revision.ToString(CultureInfo.InvariantCulture) + ".mp4");
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return null;
        }

        await _blackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            Directory.CreateDirectory(_root);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";
            var (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, BumperArguments.BuildBlackArguments(w, h, BlackSeconds, temp), captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                _logger.LogWarning("Live Channels: could not render the black filler (exit {Exit}): {Error}", exit, error.Trim());
                return null;
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally
        {
            _blackGate.Release();
        }
    }

    /// <summary>
    /// A stand-in library item for a rendered clip (a bumper or the black filler), so the stream plays it through
    /// the same per-item path as content. It has no library id, so nothing is learned or looked up for it.
    /// </summary>
    /// <param name="path">The clip.</param>
    /// <param name="title">The label it is logged under.</param>
    /// <param name="width">The channel's output width the clip was rendered for.</param>
    /// <param name="seconds">The clip's length.</param>
    /// <returns>The stand-in item.</returns>
    internal static ProgramEntry ClipEntry(string path, string title, int width, double seconds)
        => new(Guid.Empty, title, null, TimeSpan.FromSeconds(seconds).Ticks, path)
        {
            SourceHeight = IntroService.FrameSize(width).Height,
            SourceVideoCodec = "h264",
            SourceColorSpace = "bt709",
            SourceAudioCodec = "aac",
            SourceAudioChannels = 2,
            DefaultAudioOrdinal = 0,
            ItemKind = "Bumper",
        };

    /// <summary>
    /// The artwork a bumper shows, by the first that exists: the item's landscape image (an episode's own still or
    /// thumbnail, else its series' thumbnail or backdrop), then its backdrop, then its poster. The frame takes the
    /// chosen image's own shape, so square art gets a square frame. <c>null</c> means a text-only card.
    /// </summary>
    /// <param name="program">The item.</param>
    /// <returns>The artwork's path, or <c>null</c>.</returns>
    internal static string? ChooseArt(ProgramEntry program)
        => new[] { program.ThumbImagePath, program.BackdropImagePath, program.PrimaryImagePath }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    /// <inheritdoc />
    public void Dispose()
    {
        _renderGate.Dispose();
        _blackGate.Dispose();
    }

    /// <summary>
    /// Renders an item's bumper to <paramref name="output"/>: the channel layer (from the cache, rendered first when
    /// missing), the item's shapes, then the composite. Exposed for tests; the stream uses <see cref="Prepare"/>.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="program">The item.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <param name="output">Where the bumper is written.</param>
    /// <returns>Whether the bumper was rendered.</returns>
    internal async Task<bool> RenderAsync(Channel channel, ProgramEntry program, int width, string? logoPath, string output)
    {
        var ffmpeg = _encoder.EncoderPath;
        var fonts = Fonts();
        if (string.IsNullOrEmpty(ffmpeg) || fonts is null)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var (w, h) = IntroService.FrameSize(width);
        var layer = await EnsureLayerAsync(ffmpeg, channel, w, h, logoPath, fonts).ConfigureAwait(false);
        if (layer is null)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, "render-" + Guid.NewGuid().ToString("N"));
        var shapes = BumperShapeFiles.Under(prefix);
        var texts = new List<string>();
        var temp = prefix + ".tmp.mp4";
        try
        {
            var art = ChooseArt(program);
            var artInput = art is null ? null : IntroService.LogoInput(art, BumperArguments.Seconds);
            var shape = artInput is null ? ArtShape.None : await ProbeShapeAsync(art!).ConfigureAwait(false);
            if (shape == ArtShape.None)
            {
                artInput = null;
            }

            var layout = BumperLayout.Compute(BumperContent.For(program), shape, w, h, fonts);
            if (BumperArguments.BuildShapeArguments(layout, shapes) is { } shapeArgs)
            {
                var (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, shapeArgs, captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
                if (exit != 0)
                {
                    _logger.LogWarning("Live Channels: Up Next bumper shapes failed for \"{Title}\" on {Name} (exit {Exit}): {Error}", program.Title, channel.Name, exit, error.Trim());
                    return false;
                }
            }

            string TextFile(string text)
            {
                var file = prefix + ".text" + texts.Count.ToString(CultureInfo.InvariantCulture) + ".txt";
                File.WriteAllText(file, text);
                texts.Add(file);
                return file;
            }

            var args = BumperArguments.BuildItemArguments(layout, layer, artInput, shapes, TextFile, temp);
            var (code, _, stderr) = await IntroService.RunProcessAsync(ffmpeg, args, captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                _logger.LogWarning("Live Channels: Up Next bumper render failed for \"{Title}\" on {Name} (exit {Exit}): {Error}", program.Title, channel.Name, code, stderr.Trim());
                return false;
            }

            File.Move(temp, output, overwrite: true);
            _logger.LogInformation("Live Channels: rendered the Up Next bumper for \"{Title}\" on {Name} in {Seconds:F1}s", program.Title, channel.Name, stopwatch.Elapsed.TotalSeconds);
            return true;
        }
        finally
        {
            TryDelete(temp);
            foreach (var file in new[] { shapes.ArtMask, shapes.Shadow, shapes.Pill, shapes.BadgeFill, shapes.BadgeRing }.Concat(texts))
            {
                TryDelete(file);
            }
        }
    }

    private async Task RenderAndForgetAsync(Channel channel, ProgramEntry program, int width, string? logoPath, string path)
    {
        try
        {
            await _renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
                    await RenderAsync(channel, program, width, logoPath, path).ConfigureAwait(false);
                }
            }
            finally
            {
                _renderGate.Release();
            }
        }
        catch (Exception ex)
        {
            // A bumper is a nicety: a failed render only means its slot shows black, and must surface nowhere else.
            _logger.LogWarning(ex, "Live Channels: could not render the Up Next bumper for \"{Title}\" on {Name}", program.Title, channel.Name);
        }
        finally
        {
            _inFlight.TryRemove(path, out _);
        }
    }

    // The channel's layer (backdrop, lines, header and chord) at this frame size and logo, rendered when missing.
    private async Task<string?> EnsureLayerAsync(string ffmpeg, Channel channel, int width, int height, string? logoPath, BumperFonts fonts)
    {
        var directory = ChannelDir(channel);
        var key = Hash(string.Join('|', "layer", Revision.ToString(CultureInfo.InvariantCulture), width.ToString(CultureInfo.InvariantCulture), IntroService.LogoStamp(logoPath)));
        var path = Path.Combine(directory, "layer-" + key + ".mp4");
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, "layer-" + Guid.NewGuid().ToString("N"));
        var strips = new LineStrips(prefix, width, height);
        var scrim = prefix + ".scrim.png";
        var caption = prefix + ".caption.txt";
        var temp = prefix + ".tmp.mp4";
        try
        {
            var (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, IntroService.BuildStripArguments(strips), captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
            if (exit != 0 || !strips.AllExist())
            {
                _logger.LogWarning("Live Channels: Up Next layer lines failed for {Name} (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                return null;
            }

            (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, BumperArguments.BuildScrimArguments(width, height, scrim), captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(scrim))
            {
                _logger.LogWarning("Live Channels: Up Next layer scrim failed for {Name} (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                return null;
            }

            await File.WriteAllTextAsync(caption, "Up Next").ConfigureAwait(false);
            var font = fonts.For(BumperFace.DisplaySemiBold, "Up Next").Path;
            var args = BumperArguments.BuildLayerArguments(strips, scrim, IntroService.LogoInput(logoPath, BumperArguments.Seconds), font, caption, height / 720.0, temp);
            (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, args, captureOutput: false, RenderPassTimeout).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                _logger.LogWarning("Live Channels: Up Next layer render failed for {Name} (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                return null;
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally
        {
            TryDelete(temp);
            TryDelete(scrim);
            TryDelete(caption);
            foreach (var file in strips.Files)
            {
                TryDelete(file);
            }
        }
    }

    // The artwork frame's shape, from the image's own size (probed, since only the file knows it).
    private async Task<ArtShape> ProbeShapeAsync(string image)
    {
        var ffprobe = _encoder.ProbePath;
        if (string.IsNullOrEmpty(ffprobe))
        {
            return ArtShape.None;
        }

        var (_, stdout, _) = await IntroService.RunProcessAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", image], captureOutput: true, ProbeTimeout).ConfigureAwait(false);
        var parts = stdout.Trim().Split('\n')[0].Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                ? BumperLayout.ShapeFor(w, h)
                : ArtShape.None;
    }

    private BumperFonts? Fonts()
    {
        lock (_fontsLock)
        {
            if (_fonts is null)
            {
                try
                {
                    _fonts = BumperFonts.Extract(_fontsDir, FontLocator.Find());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Live Channels: could not write the Up Next bumper fonts to {Directory}", _fontsDir);
                }
            }

            return _fonts;
        }
    }

    // Where an item's bumper lives: per channel, named by item and by everything the card shows, so a change to
    // the item's metadata or artwork, the channel's logo, or the frame size renders a new one.
    private string ItemPath(Channel channel, ProgramEntry program, int width, string? logoPath)
    {
        var content = BumperContent.For(program);
        var art = ChooseArt(program);
        var key = Hash(string.Join(
            '|',
            "item",
            Revision.ToString(CultureInfo.InvariantCulture),
            width.ToString(CultureInfo.InvariantCulture),
            IntroService.LogoStamp(logoPath),
            content.Title,
            content.Tag ?? string.Empty,
            content.Subtitle ?? string.Empty,
            content.Details ?? string.Empty,
            content.Score ?? string.Empty,
            content.Rating ?? string.Empty,
            IntroService.LogoStamp(art)));
        return Path.Combine(ChannelDir(channel), program.ItemId.ToString("N", CultureInfo.InvariantCulture) + "-" + key + ".mp4");
    }

    private string ChannelDir(Channel channel) => Path.Combine(_root, "ch" + channel.Number.ToString(CultureInfo.InvariantCulture));

    // Deletes bumpers, layers and fillers nobody has used for a while, and leftovers of interrupted renders. Runs
    // at most hourly, off the caller's thread.
    private void MaybePrune()
    {
        var now = DateTime.UtcNow.Ticks;
        var due = Interlocked.Read(ref _nextPruneTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextPruneTicks, now + PruneInterval.Ticks, due) != due)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_root))
                {
                    return;
                }

                var cutoff = DateTime.UtcNow - KeepUnused;
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    var stale = File.GetLastWriteTimeUtc(file) < cutoff;
                    var leftover = file.Contains(".tmp", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - TimeSpan.FromHours(1);
                    if ((stale || leftover) && !_inFlight.ContainsKey(file))
                    {
                        TryDelete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Live Channels: could not prune the Up Next bumpers");
            }
        });
    }

    // Marks a cached file as used, so the prune keeps it.
    private void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not touch {Path}", path);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not delete {Path}", path);
        }
    }

    private static string Hash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash = (hash ^ (byte)c) * 16777619u;
                hash = (hash ^ (byte)(c >> 8)) * 16777619u;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
