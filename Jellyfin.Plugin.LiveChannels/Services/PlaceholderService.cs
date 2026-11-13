using System;
using System.Collections.Generic;
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
/// The placeholder cards: for each placeholder block, a ten-second scene that loops without a seam, played back to
/// back for as long as the block's window lasts. The scene (the style, the channel logo, and room for the message)
/// is rendered by the Generate Buffer Cards task and cached in the assets folder of the plugin's stream root; the
/// message is drawn onto it while it plays, so each airing names its own resume time and content without a render.
/// A scene is a plain intermediate video the stream plays through the channel's own encoder, like an Up Next card,
/// so one render serves every output setting of that frame size.
/// </summary>
public sealed class PlaceholderService : IDisposable
{
    /// <summary>The length of one play of a placeholder card.</summary>
    public static readonly TimeSpan CardLength = TimeSpan.FromSeconds(PlaceholderArguments.Seconds);

    // Bump whenever a scene's rendering changes, so cached scenes regenerate.
    private const int Revision = 6;

    // Scenes no block uses any more, and message files nobody has drawn, are deleted once unused for this long when
    // the cards next render: a session still open on an older look or output size keeps playing its own scene.
    private static readonly TimeSpan KeepUnused = TimeSpan.FromDays(3);

    // A pass renders ten seconds of video at idle priority behind every live encode, so a busy server can stretch it
    // to minutes; only past this is it hung rather than starved.
    private static readonly TimeSpan RenderPassTimeout = TimeSpan.FromMinutes(10);

    private readonly IMediaEncoder _encoder;
    private readonly string _root;
    private readonly string _fontsDir;
    private readonly ILogger<PlaceholderService> _logger;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly object _fontsLock = new();
    private BumperFonts? _fonts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaceholderService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="appPaths">Application paths; scenes live in the assets folder of the plugin's stream root.</param>
    /// <param name="logger">The logger.</param>
    public PlaceholderService(IMediaEncoder encoder, IApplicationPaths appPaths, ILogger<PlaceholderService> logger)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _encoder = encoder;
        _logger = logger;
        var assets = PluginPaths.AssetsDir(appPaths);
        _root = Path.Combine(assets, "placeholders");
        _fontsDir = Path.Combine(assets, "fonts");
    }

    /// <summary>
    /// The block's rendered scene for this channel, frame width and logo, or <c>null</c> when it is not rendered.
    /// Never renders: the stream must not wait for a render.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="block">The placeholder block.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <returns>The scene's video and the style it was rendered in (a custom block whose file was missing renders
    /// the default look), or <c>null</c>. Reads only the plugin's own cache, never the custom file, so a share that
    /// has stopped answering cannot hold up the stream.</returns>
    public (string Path, PlaceholderStyle Style)? TryGetCached(Channel channel, RatingBlock block, int width, string? logoPath)
    {
        var path = ScenePath(channel, block, width, logoPath);
        if (!File.Exists(path) || ReadSidecar(path) is not { } sidecar)
        {
            return null;
        }

        Touch(path);
        Touch(SidecarPath(path));
        return (path, sidecar.Style);
    }

    /// <summary>
    /// Renders every placeholder block's scene of the channel that is not cached already, one at a time and at idle
    /// priority, and deletes the channel's scenes no block uses any more. A channel without placeholder blocks
    /// renders nothing. Never throws for a failed render; the block then plays its message over black.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the renders still to start.</param>
    /// <returns>A task that completes when the channel's scenes are current.</returns>
    public async Task EnsureAsync(Channel channel, int width, string? logoPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var blocks = channel.PlaceholderBlocks();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (blocks.Count == 0)
        {
            // Nothing to render for a channel without placeholder blocks; only cards left from removed ones go.
            RemoveUnused(channel, keep);
            return;
        }

        foreach (var block in blocks)
        {
            var path = ScenePath(channel, block, width, logoPath);
            keep.Add(path);
            keep.Add(SidecarPath(path));
            if (IsCurrent(path, block) || string.IsNullOrEmpty(_encoder.EncoderPath))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(path, block) && !await RenderAsync(channel, block, width, logoPath, path).ConfigureAwait(false))
                {
                    _logger.LogWarning("Live Channels: could not render the {Style} placeholder card for {Name}; it plays over black", block.PlaceholderStyle, channel.Name);
                }
            }
            finally
            {
                _renderGate.Release();
            }
        }

        RemoveUnused(channel, keep);
        PruneText();
    }

    /// <summary>
    /// The block's message as filters drawn onto its scene (or the black filler) while it plays: the title, and the
    /// body with <c>{time}</c> and <c>{contentName}</c> filled in. A body naming the content is left off when what
    /// follows is not known; the title then keeps its place on the scene. <c>null</c> when there is nothing to
    /// draw or the fonts cannot be read.
    /// </summary>
    /// <param name="block">The placeholder block.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo the scene was rendered with, or <c>null</c>.</param>
    /// <param name="resumeLocal">When the channel resumes, in local time.</param>
    /// <param name="contentName">What it resumes with, or <c>null</c> when that is not known.</param>
    /// <param name="sceneStyle">The style the scene was rendered in (see <see cref="TryGetCached"/>), or <c>null</c> when the message is drawn onto black (centred, no logo).</param>
    /// <returns>The filters, ending in a comma, or <c>null</c>.</returns>
    internal string? TextOverlay(RatingBlock block, int width, string? logoPath, DateTime resumeLocal, string? contentName, PlaceholderStyle? sceneStyle)
    {
        ArgumentNullException.ThrowIfNull(block);
        var title = (block.PlaceholderTitle ?? string.Empty).Trim();
        var body = (block.PlaceholderBody ?? string.Empty).Trim();
        body = string.IsNullOrEmpty(contentName) && body.Contains("{contentName}", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : PlaceholderArguments.ResolveMessage(body, resumeLocal, contentName ?? string.Empty, CultureInfo.CurrentCulture);

        if (title.Length == 0 && body.Length == 0)
        {
            return null;
        }

        var fonts = Fonts();
        if (fonts is null)
        {
            return null;
        }

        try
        {
            var texts = Path.Combine(_root, "text");
            Directory.CreateDirectory(texts);
            var (frameWidth, height) = IntroService.FrameSize(width);
            var room = frameWidth * 9 / 10;
            (string Font, string File, int Size)? Line(string text, BumperFace face, int size)
            {
                if (text.Length == 0)
                {
                    return null;
                }

                (text, size) = Fit(text, fonts.For(face, text).Metrics, size, room);
                var file = Path.Combine(texts, Hash(text) + ".txt");
                WriteOnce(file, text);

                return (fonts.For(face, text).Path, file, size);
            }

            var titleLine = Line(title, BumperFace.DisplayBold, PlaceholderArguments.TitleSize(height));
            var bodyLine = Line(body, BumperFace.Medium, PlaceholderArguments.BodySize(height));
            return sceneStyle is { } style
                ? PlaceholderArguments.TextOverlay(style, height, logoPath, titleLine, bodyLine, HasTitle(block), HasBody(block))
                : PlaceholderArguments.TextOverlay(PlaceholderStyle.Custom, height, null, titleLine, bodyLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not write a placeholder message");
            return null;
        }
    }

    /// <summary>Deletes every cached placeholder scene and message, so the next Generate Buffer Cards run renders them afresh.</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
                _logger.LogInformation("Live Channels: cleared the cached placeholder cards");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Live Channels: could not clear the cached placeholder cards");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _renderGate.Dispose();

    /// <summary>
    /// Where a block's scene is cached: by channel, then by everything configured for the scene, so two blocks that
    /// look the same share one render and any change to the look renders a new one. Worked out from the settings
    /// alone (a custom file's own changes are tracked in the record beside the scene), so finding a scene never
    /// opens the custom file.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="block">The placeholder block.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <returns>The scene's path.</returns>
    internal string ScenePath(Channel channel, RatingBlock block, int width, string? logoPath)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(block);
        var style = block.PlaceholderStyle;
        var key = Hash(string.Join(
            '\u001f',
            Revision.ToString(CultureInfo.InvariantCulture),
            width.ToString(CultureInfo.InvariantCulture),
            style.ToString(),
            IntroService.LogoStamp(logoPath),
            style == PlaceholderStyle.Custom ? block.PlaceholderMediaPath ?? string.Empty : string.Empty,
            HasTitle(block) ? "t" : "-",
            HasBody(block) ? "b" : "-"));
        return Path.Combine(ChannelDir(channel), style.ToString().ToLowerInvariant() + "-" + key + ".mp4");
    }

    /// <summary>
    /// Renders a block's scene to <paramref name="output"/>: the style's prepass layers, then the scene itself.
    /// Exposed for tests; the cards task uses <see cref="EnsureAsync"/>.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="block">The placeholder block.</param>
    /// <param name="width">The channel's output width.</param>
    /// <param name="logoPath">The channel's logo, or <c>null</c>.</param>
    /// <param name="output">Where the scene is written.</param>
    /// <returns>Whether the scene was rendered.</returns>
    internal async Task<bool> RenderAsync(Channel channel, RatingBlock block, int width, string? logoPath, string output)
    {
        var ffmpeg = _encoder.EncoderPath;
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, "render-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var (w, h) = IntroService.FrameSize(width);
        var style = PlaceholderArguments.EffectiveStyle(block.PlaceholderStyle, block.PlaceholderMediaPath);
        var layers = PlaceholderArguments.Layers(style, prefix, w, h);
        var temp = prefix + ".mp4";
        try
        {
            if (PlaceholderArguments.BuildPrepassArguments(layers) is { } prepass)
            {
                var (exit, _, error) = await IntroService.RunProcessAsync(ffmpeg, prepass, captureOutput: false, RenderPassTimeout, background: true).ConfigureAwait(false);
                if (exit != 0)
                {
                    _logger.LogWarning("Live Channels: placeholder layers for {Name} failed (exit {Exit}): {Error}", channel.Name, exit, error.Trim());
                    return false;
                }
            }

            List<string> args;
            try
            {
                args = PlaceholderArguments.BuildSceneArguments(layers, logoPath, block.PlaceholderMediaPath, HasTitle(block), HasBody(block), temp);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // The custom file went away, or stopped answering, between the check and the render.
                _logger.LogWarning(ex, "Live Channels: the placeholder file for {Name} could not be read", channel.Name);
                return false;
            }

            var (code, _, stderr) = await IntroService.RunProcessAsync(ffmpeg, args, captureOutput: false, RenderPassTimeout, background: true).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                _logger.LogWarning("Live Channels: placeholder card for {Name} failed (exit {Exit}): {Error}", channel.Name, code, stderr.Trim());
                return false;
            }

            File.Move(temp, output, overwrite: true);
            await File.WriteAllTextAsync(SidecarPath(output), JsonSerializer.Serialize(new SceneSidecar(style, MediaStamp(block, style)))).ConfigureAwait(false);
            _logger.LogInformation("Live Channels: rendered the {Style} placeholder card for {Name}", style, channel.Name);
            return true;
        }
        finally
        {
            TryDelete(temp);
            foreach (var file in layers.Files)
            {
                TryDelete(file);
            }
        }
    }

    private static bool HasTitle(RatingBlock block) => !string.IsNullOrWhiteSpace(block.PlaceholderTitle);

    private static bool HasBody(RatingBlock block) => !string.IsNullOrWhiteSpace(block.PlaceholderBody);

    private BumperFonts? Fonts()
    {
        lock (_fontsLock)
        {
            return _fonts ??= BumperFonts.Extract(_fontsDir, FontLocator.Find());
        }
    }

    // Deletes the channel's scenes no current block has used for a while, and any render an interruption left behind.
    private void RemoveUnused(Channel channel, HashSet<string> keep)
    {
        var directory = ChannelDir(channel);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow - KeepUnused;
            var leftovers = DateTime.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(directory).Where(f => !keep.Contains(f) && File.GetLastWriteTimeUtc(f) < (Path.GetFileName(f).StartsWith("render-", StringComparison.Ordinal) ? leftovers : cutoff)).ToList())
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not tidy the placeholder cards of {Name}", channel.Name);
        }
    }

    /// <summary>
    /// A message line made to fit the frame: drawn smaller when it runs wider than <paramref name="room"/> (down to
    /// just over half size), and cut short with an ellipsis if it still does not fit.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="metrics">Its font's metrics.</param>
    /// <param name="size">The size it is drawn at by design.</param>
    /// <param name="room">The width it may take, in pixels.</param>
    /// <returns>The text and size to draw.</returns>
    internal static (string Text, int Size) Fit(string text, FontMetrics metrics, int size, int room)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var wide = metrics.Measure(text, size);
        if (wide <= room)
        {
            return (text, size);
        }

        var smallest = Math.Max(1, size * 55 / 100);
        var fitted = Math.Max(smallest, (int)Math.Floor(size * room / wide));
        if (metrics.Measure(text, fitted) <= room)
        {
            return (text, fitted);
        }

        // Shortened a whole character at a time (an emoji, an accented letter, a joined emoji sequence), never
        // part of one.
        var starts = new List<int>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            starts.Add(elements.ElementIndex);
        }

        var keep = starts.Count;
        var cut = text;
        while (keep > 1 && metrics.Measure(cut.TrimEnd() + "\u2026", fitted) > room)
        {
            keep--;
            cut = text[..starts[keep]];
        }

        return (cut.TrimEnd() + "\u2026", fitted);
    }

    // Whether a block's scene is rendered and still matches what it shows: a custom scene renders again when its
    // file changes, or when a file that was missing (and fell back to the default look) turns up.
    private bool IsCurrent(string path, RatingBlock block)
    {
        if (!File.Exists(path) || ReadSidecar(path) is not { } sidecar)
        {
            return false;
        }

        if (block.PlaceholderStyle != PlaceholderStyle.Custom)
        {
            return true;
        }

        var style = PlaceholderArguments.EffectiveStyle(block.PlaceholderStyle, block.PlaceholderMediaPath);
        return sidecar.Style == style && string.Equals(sidecar.MediaStamp, MediaStamp(block, style), StringComparison.Ordinal);
    }

    private static string MediaStamp(RatingBlock block, PlaceholderStyle style)
        => style == PlaceholderStyle.Custom ? IntroService.LogoStamp(block.PlaceholderMediaPath) : "none";

    private static string SidecarPath(string scene) => Path.ChangeExtension(scene, ".json");

    private SceneSidecar? ReadSidecar(string scene)
    {
        try
        {
            var file = SidecarPath(scene);
            return File.Exists(file) ? JsonSerializer.Deserialize<SceneSidecar>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Live Channels: could not read the placeholder card record beside {Scene}", scene);
            return null;
        }
    }

    // Writes a message file under its content's name, or marks one already there as used.
    private void WriteOnce(string file, string text)
    {
        if (AtomicFiles.WriteOnce(file, text))
        {
            Touch(file);
        }
    }

    // Marks a cached file as used, so the tidy-up keeps it.
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

    // Deletes message files no airing has drawn for a while: each airing writes its own resume time and content.
    private void PruneText()
    {
        var texts = Path.Combine(_root, "text");
        if (!Directory.Exists(texts))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow - KeepUnused;
            var leftovers = DateTime.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(texts).Where(f => File.GetLastWriteTimeUtc(f) < (f.EndsWith(".tmp", StringComparison.Ordinal) ? leftovers : cutoff)).ToList())
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Live Channels: could not tidy the placeholder messages");
        }
    }

    private string ChannelDir(Channel channel) => Path.Combine(_root, "ch" + channel.Number.ToString(CultureInfo.InvariantCulture));

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

    // What a rendered scene records beside itself: the style it was rendered in and the custom file it shows.
    private sealed record SceneSidecar(PlaceholderStyle Style, string MediaStamp);

    // A 64-bit FNV-1a over the UTF-8 bytes, naming scenes and message files by what they hold: each airing writes its
    // own message, so the names must not collide across the many that build up.
    private static string Hash(string value)
    {
        unchecked
        {
            var hash = 14695981039346656037ul;
            Span<byte> utf8 = stackalloc byte[4];
            foreach (var rune in value.EnumerateRunes())
            {
                var count = rune.EncodeToUtf8(utf8);
                for (var i = 0; i < count; i++)
                {
                    hash = (hash ^ utf8[i]) * 1099511628211ul;
                }
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
