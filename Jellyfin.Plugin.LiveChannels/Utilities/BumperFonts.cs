using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>The typefaces the Up Next bumper sets its text in.</summary>
internal enum BumperFace
{
    /// <summary>Outfit SemiBold: the "Up Next" caption.</summary>
    DisplaySemiBold,

    /// <summary>Outfit Bold: the title and the countdown number.</summary>
    DisplayBold,

    /// <summary>Outfit ExtraBold: the parental rating badge.</summary>
    DisplayExtraBold,

    /// <summary>Inter Regular: the year, genre and runtime line.</summary>
    Regular,

    /// <summary>Inter Medium: the episode name and the countdown label.</summary>
    Medium,

    /// <summary>Inter SemiBold: the season and episode tag and the review score.</summary>
    SemiBold,

    /// <summary>Inter Medium Italic: a movie's tagline.</summary>
    MediumItalic,
}

/// <summary>
/// The bumper's fonts, bundled with the plugin and written out once to its assets folder so ffmpeg's drawtext can
/// read them by path, with each one's metrics for measuring text. A piece of text that the bundled font has no
/// glyph for (a title in a script Inter and Outfit do not cover) is set in a system font instead, when one is
/// found, rather than drawn as missing-glyph boxes.
/// </summary>
internal sealed class BumperFonts
{
    private static readonly Dictionary<BumperFace, string> Files = new()
    {
        [BumperFace.DisplaySemiBold] = "Outfit-SemiBold.ttf",
        [BumperFace.DisplayBold] = "Outfit-Bold.ttf",
        [BumperFace.DisplayExtraBold] = "Outfit-ExtraBold.ttf",
        [BumperFace.Regular] = "Inter-Regular.ttf",
        [BumperFace.Medium] = "Inter-Medium.ttf",
        [BumperFace.SemiBold] = "Inter-SemiBold.ttf",
        [BumperFace.MediumItalic] = "Inter-MediumItalic.ttf",
    };

    private readonly Dictionary<BumperFace, (string Path, FontMetrics Metrics)> _faces;
    private readonly (string Path, FontMetrics Metrics)? _fallback;

    private BumperFonts(Dictionary<BumperFace, (string Path, FontMetrics Metrics)> faces, (string Path, FontMetrics Metrics)? fallback)
    {
        _faces = faces;
        _fallback = fallback;
    }

    /// <summary>
    /// Writes the bundled fonts into <paramref name="directory"/> (each rewritten when its size differs from the
    /// bundled one, so a stale or truncated copy never reaches the font parser) and reads their metrics.
    /// </summary>
    /// <param name="directory">Where the fonts are written.</param>
    /// <param name="fallbackFont">A system font for text the bundled fonts cannot draw, or <c>null</c>.</param>
    /// <returns>The fonts, or <c>null</c> when a bundled font is missing or unreadable.</returns>
    public static BumperFonts? Extract(string directory, string? fallbackFont)
    {
        Directory.CreateDirectory(directory);
        var assembly = typeof(BumperFonts).Assembly;
        var resources = assembly.GetManifestResourceNames();
        var faces = new Dictionary<BumperFace, (string Path, FontMetrics Metrics)>();
        foreach (var (face, file) in Files)
        {
            var resource = resources.FirstOrDefault(n => n.EndsWith(".Fonts." + file, StringComparison.Ordinal));
            if (resource is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return null;
            }

            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(output);
                }

                File.Move(temp, path, overwrite: true);
            }

            if (FontMetrics.Load(path) is not { } metrics)
            {
                return null;
            }

            faces[face] = (path, metrics);
        }

        (string, FontMetrics)? fallback = null;
        if (!string.IsNullOrEmpty(fallbackFont) && FontMetrics.Load(fallbackFont) is { } fallbackMetrics)
        {
            fallback = (fallbackFont, fallbackMetrics);
        }

        return new BumperFonts(faces, fallback);
    }

    /// <summary>The font file and metrics to set a piece of text in: the face asked for, or the system fallback when the face cannot draw the text and the fallback can.</summary>
    /// <param name="face">The face the design calls for.</param>
    /// <param name="text">The text.</param>
    /// <returns>The font file and its metrics.</returns>
    public (string Path, FontMetrics Metrics) For(BumperFace face, string text)
    {
        var chosen = _faces[face];
        if (_fallback is { } fallback && !chosen.Metrics.Covers(text) && fallback.Metrics.Covers(text))
        {
            return fallback;
        }

        return chosen;
    }

    /// <summary>The metrics of a face.</summary>
    /// <param name="face">The face.</param>
    /// <returns>Its metrics.</returns>
    public FontMetrics Metrics(BumperFace face) => _faces[face].Metrics;
}
