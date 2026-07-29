using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Builds the libass <c>force_style</c> override the burn-in filters apply to every subtitle style. Only the
/// options the viewer actually set are emitted, so an untouched configuration renders the subtitle file exactly
/// as its author wrote it (including inline bold/italic/colour tags, which are per-event overrides that always
/// win over a style). Pure (no Jellyfin dependencies) so the override string can be unit tested directly.
/// </summary>
public static class SubtitleStyle
{
    /// <summary>The smallest and largest accepted relative size, as a percentage of the subtitle's own size.</summary>
    public const int MinScalePercent = 50;

    /// <summary>The largest accepted relative size, as a percentage of the subtitle's own size.</summary>
    public const int MaxScalePercent = 300;

    /// <summary>
    /// Builds the <c>force_style</c> value for the configured appearance, or <c>null</c> when nothing was
    /// customised (in which case the filters omit the option entirely).
    /// </summary>
    /// <param name="font">The font family name, or empty to keep the subtitle's own font.</param>
    /// <param name="scalePercent">The size as a percentage of the subtitle's own size; 100 (or out of range) leaves it alone.</param>
    /// <param name="textColor">The text colour as <c>#RRGGBB</c>, or empty to keep the subtitle's own colour.</param>
    /// <param name="outlineColor">The outline/box colour as <c>#RRGGBB</c>, or empty to keep the subtitle's own colour.</param>
    /// <param name="border">How the text is separated from the picture.</param>
    /// <param name="bold">Whether to draw the text bold.</param>
    /// <returns>The <c>force_style</c> value, or <c>null</c>.</returns>
    public static string? Build(string? font, int scalePercent, string? textColor, string? outlineColor, SubtitleBorderStyle border, bool bold)
    {
        var parts = new List<string>(8);

        var family = SanitizeFontName(font);
        if (family.Length > 0)
        {
            parts.Add("FontName=" + family);
        }

        // The size is expressed as a glyph scale, not an absolute Fontsize: a subtitle's own size is authored
        // against its script resolution (and a converted SRT is sized from the video), so one absolute number
        // would mean something different for every source.
        //
        // The value is a multiplier, NOT a percentage, even though the ASS style field is written in percent:
        // libass divides ScaleX/ScaleY by 100 when it parses a Style line, but a force_style override is applied
        // afterwards and stored raw. Writing 150 here would scale the glyphs 150 times, not by half again.
        if (scalePercent is >= MinScalePercent and <= MaxScalePercent && scalePercent != 100)
        {
            var scale = (scalePercent / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
            parts.Add("ScaleX=" + scale);
            parts.Add("ScaleY=" + scale);
        }

        if (TryConvertColor(textColor, out var primary))
        {
            parts.Add("PrimaryColour=" + primary);
        }

        if (TryConvertColor(outlineColor, out var outline))
        {
            // libass paints the opaque-box background with the outline colour too, so one setting covers both
            // border styles.
            parts.Add("OutlineColour=" + outline);
        }

        switch (border)
        {
            case SubtitleBorderStyle.Outline:
                parts.Add("BorderStyle=1");
                parts.Add("Outline=2");
                parts.Add("Shadow=1");
                break;
            case SubtitleBorderStyle.Box:
                parts.Add("BorderStyle=3");
                parts.Add("Outline=1");
                parts.Add("Shadow=0");
                break;
            default:
                break;
        }

        if (bold)
        {
            parts.Add("Bold=-1");
        }

        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    /// <summary>
    /// Converts an HTML colour (<c>#RRGGBB</c>, with or without the hash) to the ASS <c>&amp;HAABBGGRR</c> form
    /// libass expects: byte-reversed, fully opaque.
    /// </summary>
    /// <param name="html">The colour to convert.</param>
    /// <param name="ass">The converted colour when the input was a valid six-digit hex colour.</param>
    /// <returns>Whether the colour was converted.</returns>
    public static bool TryConvertColor(string? html, out string ass)
    {
        ass = string.Empty;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var hex = html.Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return false;
        }

        for (var i = 0; i < hex.Length; i++)
        {
            if (!Uri.IsHexDigit(hex[i]))
            {
                return false;
            }
        }

        // #RRGGBB -> &H00BBGGRR (alpha 00 is fully opaque in ASS).
        ass = "&H00" + hex[4..6].ToUpperInvariant() + hex[2..4].ToUpperInvariant() + hex[0..2].ToUpperInvariant();
        return true;
    }

    // A font family name is spliced straight into a filter argument, so keep it to characters that cannot end the
    // option, the style entry, or the quoted filter string.
    private static string SanitizeFontName(string? font)
    {
        if (string.IsNullOrWhiteSpace(font))
        {
            return string.Empty;
        }

        var clean = new List<char>(font.Length);
        foreach (var c in font.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            {
                clean.Add(c);
            }
        }

        return new string(clean.ToArray()).Trim();
    }
}
