using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// The horizontal metrics of a TrueType font, read straight from its tables: enough to measure a line of text the
/// way ffmpeg's drawtext lays it out (glyph advances, no shaping), so the Up Next bumper can wrap, shrink and
/// ellipsize titles before it hands them to ffmpeg, and tell when a font has no glyph for a character. Reads the
/// <c>head</c>, <c>hhea</c>, <c>hmtx</c> and <c>cmap</c> tables of a <c>.ttf</c>, or the first font of a
/// <c>.ttc</c> collection.
/// </summary>
internal sealed class FontMetrics
{
    private readonly ushort[] _advances;
    private readonly Dictionary<int, int> _glyphs;

    private FontMetrics(int unitsPerEm, int ascender, int descender, ushort[] advances, Dictionary<int, int> glyphs)
    {
        UnitsPerEm = unitsPerEm;
        Ascender = ascender;
        Descender = descender;
        _advances = advances;
        _glyphs = glyphs;
    }

    /// <summary>Gets the font's design units per em.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Gets the font's ascender in design units (positive, above the baseline).</summary>
    public int Ascender { get; }

    /// <summary>Gets the font's descender in design units (negative, below the baseline).</summary>
    public int Descender { get; }

    /// <summary>Reads a font file, or returns <c>null</c> when it is missing or not a font these tables can describe.</summary>
    /// <param name="path">The font file.</param>
    /// <returns>The metrics, or <c>null</c>.</returns>
    public static FontMetrics? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a font from its bytes, or returns <c>null</c> when they are not a font these tables can describe.</summary>
    /// <param name="data">The font file's bytes.</param>
    /// <returns>The metrics, or <c>null</c>.</returns>
    public static FontMetrics? Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            var font = 0;
            if (data.Length >= 16 && Tag(data, 0) == "ttcf")
            {
                font = (int)U32(data, 12);
            }

            var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
            var count = U16(data, font + 4);
            for (var i = 0; i < count; i++)
            {
                var record = font + 12 + (i * 16);
                tables[Tag(data, record)] = ((int)U32(data, record + 8), (int)U32(data, record + 12));
            }

            if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea)
                || !tables.TryGetValue("hmtx", out var hmtx) || !tables.TryGetValue("cmap", out var cmap))
            {
                return null;
            }

            var unitsPerEm = U16(data, head.Offset + 18);
            var ascender = (short)U16(data, hhea.Offset + 4);
            var descender = (short)U16(data, hhea.Offset + 6);
            var metricCount = U16(data, hhea.Offset + 34);
            if (unitsPerEm == 0 || metricCount == 0)
            {
                return null;
            }

            var advances = new ushort[metricCount];
            for (var i = 0; i < metricCount; i++)
            {
                advances[i] = U16(data, hmtx.Offset + (i * 4));
            }

            var glyphs = ReadCmap(data, cmap.Offset);
            return glyphs is null ? null : new FontMetrics(unitsPerEm, ascender, descender, advances, glyphs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Whether the font has a glyph for every character of the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether nothing would draw as a missing-glyph box.</returns>
    public bool Covers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && !_glyphs.ContainsKey(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The width of a line of text at a font size, in pixels.</summary>
    /// <param name="text">The text.</param>
    /// <param name="size">The font size in pixels (the em height).</param>
    /// <param name="letterSpacing">Extra space after every character, in pixels.</param>
    /// <returns>The advance width in pixels.</returns>
    public double Measure(string text, double size, double letterSpacing = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        long units = 0;
        var characters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = _glyphs.TryGetValue(rune.Value, out var g) ? g : 0;
            units += _advances[Math.Min(glyph, _advances.Length - 1)];
            characters++;
        }

        return (units * size / UnitsPerEm) + (characters * letterSpacing);
    }

    /// <summary>The ascent at a font size, in pixels.</summary>
    /// <param name="size">The font size in pixels.</param>
    /// <returns>The ascent above the baseline.</returns>
    public double Ascent(double size) => Ascender * size / UnitsPerEm;

    /// <summary>The descent at a font size, in pixels (positive, below the baseline).</summary>
    /// <param name="size">The font size in pixels.</param>
    /// <returns>The descent below the baseline.</returns>
    public double Descent(double size) => -Descender * size / UnitsPerEm;

    // The best Unicode subtable: a full-repertoire format 12 one when the font has it, else the BMP format 4 one.
    private static Dictionary<int, int>? ReadCmap(byte[] data, int cmap)
    {
        var count = U16(data, cmap + 2);
        int? bmp = null;
        int? full = null;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + (i * 8);
            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var offset = cmap + (int)U32(data, record + 4);
            var format = U16(data, offset);
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!unicode)
            {
                continue;
            }

            if (format == 12)
            {
                full ??= offset;
            }
            else if (format == 4)
            {
                bmp ??= offset;
            }
        }

        if (full is { } twelve)
        {
            return ReadFormat12(data, twelve);
        }

        return bmp is { } four ? ReadFormat4(data, four) : null;
    }

    private static Dictionary<int, int> ReadFormat12(byte[] data, int table)
    {
        var groups = (int)U32(data, table + 12);
        var map = new Dictionary<int, int>();
        for (var i = 0; i < groups; i++)
        {
            var group = table + 16 + (i * 12);
            var start = (int)U32(data, group);
            var end = (int)U32(data, group + 4);
            var glyph = (int)U32(data, group + 8);

            // A real font's groups are small; the cap keeps a corrupt one from allocating without bound.
            for (var c = start; c <= end && c - start < 0x10000; c++)
            {
                map[c] = glyph + (c - start);
            }
        }

        return map;
    }

    private static Dictionary<int, int> ReadFormat4(byte[] data, int table)
    {
        var segments = U16(data, table + 6) / 2;
        var ends = table + 14;
        var starts = ends + (segments * 2) + 2;
        var deltas = starts + (segments * 2);
        var ranges = deltas + (segments * 2);
        var map = new Dictionary<int, int>();
        for (var s = 0; s < segments; s++)
        {
            var end = U16(data, ends + (s * 2));
            var start = U16(data, starts + (s * 2));
            var delta = (short)U16(data, deltas + (s * 2));
            var rangeAt = ranges + (s * 2);
            var range = U16(data, rangeAt);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (range == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = rangeAt + range + ((c - start) * 2);
                    glyph = U16(data, at);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    map[c] = glyph;
                }
            }
        }

        return map;
    }

    private static string Tag(byte[] data, int at) => Encoding.ASCII.GetString(data, at, 4);

    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2));

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
}
