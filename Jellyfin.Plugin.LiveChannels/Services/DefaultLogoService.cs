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
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Generates a fallback channel logo when a channel has no uploaded image: a 1080x1080 square in a colour
/// derived deterministically from the channel name, with either the channel number or a Material Icons symbol
/// centred and, optionally, the channel name along the bottom. Rendered with ffmpeg and cached in memory.
/// </summary>
public class DefaultLogoService
{
    private const int Size = 1080;

    // Maximum characters per title line; a line wider than this is wrapped, abbreviated, or dropped.
    private const int TitleMaxCharsPerLine = 15;

    private static readonly char[] TitleSeparators = { ' ', '\t', '-', '_', '.', ':', '/' };

    // The embedded Material Icons font and its name-to-codepoint map are loaded once, process-wide.
    private static readonly object IconLock = new();
    private static string? _iconFontPath;
    private static Dictionary<string, int>? _iconCodepoints;

    private readonly IMediaEncoder _encoder;
    private readonly ILogger<DefaultLogoService> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultLogoService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="logger">The logger.</param>
    public DefaultLogoService(IMediaEncoder encoder, ILogger<DefaultLogoService> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>
    /// Returns the PNG bytes of the generated logo for a channel, or <c>null</c> when it cannot be produced.
    /// </summary>
    /// <param name="number">The channel number, drawn in the centre for the number style.</param>
    /// <param name="name">The channel name, hashed to pick the colour and (optionally) drawn along the bottom.</param>
    /// <param name="style">Whether the centre shows the number or a Material Icons symbol.</param>
    /// <param name="symbol">The Material Icons name drawn for the symbol style (e.g. <c>arrow_back</c>).</param>
    /// <param name="showName">Whether the channel name is drawn along the bottom.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The PNG bytes, or <c>null</c>.</returns>
    public async Task<byte[]?> GetAsync(int number, string name, LogoStyle style, string symbol, bool showName, CancellationToken cancellationToken)
    {
        name ??= string.Empty;
        symbol ??= string.Empty;
        var key = string.Join('|', number.ToString(CultureInfo.InvariantCulture), name, (int)style, symbol, showName ? "1" : "0");
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bytes = await GenerateAsync(number, name, style, symbol, showName, cancellationToken).ConfigureAwait(false);
        if (bytes is { Length: > 0 })
        {
            _cache[key] = bytes;
            return bytes;
        }

        return null;
    }

    private async Task<byte[]?> GenerateAsync(int number, string name, LogoStyle style, string symbol, bool showName, CancellationToken cancellationToken)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return null;
        }

        var (background, text) = ColorsFor(name);
        var fc = "0x" + text;
        var textFont = FontLocator.Find();

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=0x" + background + ":s=" + Size + "x" + Size
        };

        var filters = new List<string>();

        // Centre: a Material Icons symbol when chosen and resolvable, otherwise the channel number.
        var (centreFont, centreText) = ResolveCentre(number, style, symbol, textFont);
        if (centreFont is not null && centreText.Length > 0)
        {
            var safeCentreFont = centreFont.Replace("\\", "/", StringComparison.Ordinal);
            var centreSize = (Size * 2 / 5).ToString(CultureInfo.InvariantCulture);
            filters.Add("drawtext=fontfile='" + safeCentreFont + "':text='" + SanitizeForDrawtext(centreText)
                + "':fontcolor=" + fc + ":fontsize=" + centreSize + ":x=(w-tw)/2:y=(h-th)/2");
        }

        // Name along the bottom, on one or two lines (or dropped) so it never overflows.
        if (showName && textFont is not null)
        {
            var safeFont = textFont.Replace("\\", "/", StringComparison.Ordinal);
            var lines = FitTitleLines(name);
            var titleSize = (Size / 13).ToString(CultureInfo.InvariantCulture);
            var bottom = Size / 11;
            var lineHeight = Size * 7 / 90;
            for (var i = 0; i < lines.Length; i++)
            {
                // Stack upward from the bottom line so two rows sit just above the lower edge.
                var offset = bottom + ((lines.Length - 1 - i) * lineHeight);
                filters.Add("drawtext=fontfile='" + safeFont + "':text='" + SanitizeForDrawtext(lines[i])
                    + "':fontcolor=" + fc + ":fontsize=" + titleSize + ":x=(w-tw)/2:y=h-th-"
                    + offset.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(',', filters));
        }

        args.Add("-frames:v");
        args.Add("1");
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("png");
        args.Add("pipe:1");

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

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start ffmpeg to generate a default logo for channel {Number}", number);
            return null;
        }

        using var buffer = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Ensure ffmpeg never lingers if the request was cancelled mid-generation.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // Reap the killed process so it doesn't linger (briefly) as a zombie on Unix. The logo ffmpeg
                    // synthesises an image (no media-file input), so a SIGKILL'd one exits at once.
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not kill the default-logo ffmpeg process");
            }
        }

        if (buffer.Length == 0)
        {
            // The process has already exited/been killed; read its error without the request token so a late
            // cancellation can't throw out of here (we are returning null regardless).
            var stderr = string.Empty;
            try
            {
                stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read default-logo ffmpeg error output");
            }

            _logger.LogWarning("ffmpeg produced no default logo for channel {Number}: {Error}", number, stderr.Trim());
            return null;
        }

        return buffer.ToArray();
    }

    // The font and text drawn in the centre: the Material Icons glyph for a known symbol name, otherwise the
    // channel number in the regular font (also the fallback for an unknown symbol or a missing icon font).
    private (string? Font, string Text) ResolveCentre(int number, LogoStyle style, string symbol, string? textFont)
    {
        if (style == LogoStyle.Symbol && !string.IsNullOrWhiteSpace(symbol)
            && GetIconCodepoints(_logger).TryGetValue(symbol.Trim(), out var codepoint))
        {
            var iconFont = GetMaterialIconFontPath(_logger);
            if (iconFont is not null)
            {
                return (iconFont, char.ConvertFromUtf32(codepoint));
            }
        }

        return (textFont, number.ToString(CultureInfo.InvariantCulture));
    }

    // Extracts the embedded Material Icons font to a temp file (once) so ffmpeg's drawtext can read it by path.
    private static string? GetMaterialIconFontPath(ILogger logger)
    {
        lock (IconLock)
        {
            if (_iconFontPath is not null && File.Exists(_iconFontPath))
            {
                return _iconFontPath;
            }

            try
            {
                var assembly = typeof(DefaultLogoService).Assembly;
                var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MaterialIcons-Regular.ttf", StringComparison.Ordinal));
                if (resource is null)
                {
                    return null;
                }

                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    return null;
                }

                var path = Path.Combine(Path.GetTempPath(), "livechannels-MaterialIcons-Regular.ttf");
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(file);
                }

                _iconFontPath = path;
                return path;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not extract the Material Icons font");
                return null;
            }
        }
    }

    // Parses the embedded "name hexcode" codepoints list into a name-to-codepoint map (once).
    private static Dictionary<string, int> GetIconCodepoints(ILogger logger)
    {
        lock (IconLock)
        {
            if (_iconCodepoints is not null)
            {
                return _iconCodepoints;
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assembly = typeof(DefaultLogoService).Assembly;
                var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MaterialIcons-Regular.codepoints", StringComparison.Ordinal));
                if (resource is not null)
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = reader.ReadLine()) is not null)
                        {
                            var space = line.IndexOf(' ', StringComparison.Ordinal);
                            if (space <= 0)
                            {
                                continue;
                            }

                            var iconName = line[..space];
                            var hex = line[(space + 1)..].Trim();
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codepoint))
                            {
                                map[iconName] = codepoint;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the Material Icons codepoints");
            }

            _iconCodepoints = map;
            return map;
        }
    }

    // Fits the channel title for the bottom strip: one line if it fits, otherwise two wrapped rows, otherwise
    // an initials abbreviation, otherwise nothing. Internal for unit testing.
    internal static string[] FitTitleLines(string name)
    {
        var title = (name ?? string.Empty).Trim().ToUpperInvariant();
        if (title.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (title.Length <= TitleMaxCharsPerLine)
        {
            return new[] { title };
        }

        var words = title.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            // Two rows: greedily fill the first line, the remainder goes on the second.
            var line1 = string.Empty;
            var index = 0;
            while (index < words.Length)
            {
                var candidate = line1.Length == 0 ? words[index] : line1 + " " + words[index];
                if (candidate.Length <= TitleMaxCharsPerLine)
                {
                    line1 = candidate;
                    index++;
                }
                else
                {
                    break;
                }
            }

            if (line1.Length > 0 && index < words.Length)
            {
                var line2 = string.Join(" ", words, index, words.Length - index);
                if (line2.Length <= TitleMaxCharsPerLine)
                {
                    return new[] { line1, line2 };
                }
            }

            // Initials abbreviation.
            var acronym = string.Empty;
            foreach (var word in words)
            {
                acronym += char.ToUpperInvariant(word[0]);
            }

            if (acronym.Length is >= 2 and <= TitleMaxCharsPerLine)
            {
                return new[] { acronym };
            }
        }

        return Array.Empty<string>();
    }

    // Removes or neutralises the characters that would break ffmpeg's drawtext text argument.
    private static string SanitizeForDrawtext(string s)
        => s.Replace("\\", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("%", string.Empty, StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal);

    // Maps the channel name to a stable pastel background and a darker same-hue foreground, matching the
    // client-side preview (defaultLogoDataUrl in livechannels_channels.js) so the box and the guide agree.
    private static (string Background, string Text) ColorsFor(string name)
    {
        var hue = (int)(Fnv1a(name) % 360u);
        return (Hex(HslToRgb(hue, 0.65, 0.82)), Hex(HslToRgb(hue, 0.50, 0.32)));
    }

    private static string Hex((int R, int G, int B) rgb)
        => rgb.R.ToString("X2", CultureInfo.InvariantCulture)
            + rgb.G.ToString("X2", CultureInfo.InvariantCulture)
            + rgb.B.ToString("X2", CultureInfo.InvariantCulture);

    private static uint Fnv1a(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            return hash;
        }
    }

    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var hp = h / 60.0;
        var x = c * (1 - Math.Abs((hp % 2) - 1));
        var m = l - (c / 2);

        double r1 = 0, g1 = 0, b1 = 0;
        if (hp < 1) { r1 = c; g1 = x; }
        else if (hp < 2) { r1 = x; g1 = c; }
        else if (hp < 3) { g1 = c; b1 = x; }
        else if (hp < 4) { g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; b1 = c; }
        else { r1 = c; b1 = x; }

        return (
            (int)Math.Round((r1 + m) * 255),
            (int)Math.Round((g1 + m) * 255),
            (int)Math.Round((b1 + m) * 255));
    }
}
