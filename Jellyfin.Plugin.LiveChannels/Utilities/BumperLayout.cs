using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>The shape of the artwork frame on an Up Next bumper, taken from the chosen image's own proportions.</summary>
internal enum ArtShape
{
    /// <summary>No artwork: the card is text only.</summary>
    None,

    /// <summary>A wide 16:9 frame, for a thumbnail or backdrop.</summary>
    Landscape,

    /// <summary>A tall 2:3 frame, for a poster.</summary>
    Portrait,

    /// <summary>A square frame, for square art (album-style covers, some music videos).</summary>
    Square,
}

/// <summary>A rectangle on the frame, in pixels.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
internal readonly record struct PixelBox(int X, int Y, int Width, int Height);

/// <summary>One line of text placed on the frame.</summary>
/// <param name="Text">The text, already fitted to its room.</param>
/// <param name="Face">The face the design sets it in.</param>
/// <param name="FontPath">The font file it is drawn with (the face, or the system fallback).</param>
/// <param name="Size">The font size in pixels.</param>
/// <param name="X">The left edge in pixels, where the line comes to rest.</param>
/// <param name="Baseline">The baseline in pixels.</param>
/// <param name="Color">The drawtext colour (<c>0xRRGGBB@alpha</c>).</param>
internal readonly record struct PlacedText(string Text, BumperFace Face, string FontPath, int Size, int X, int Baseline, string Color);

/// <summary>One row of the info column: its text, the pill behind a tag, and when it slides in.</summary>
/// <param name="Texts">The row's lines (one per title line, or the pieces of a single row).</param>
/// <param name="Pill">The rounded box behind the season and episode tag, or <c>null</c>.</param>
/// <param name="Delay">Seconds into the bumper the row starts sliding in.</param>
internal sealed record PlacedRow(IReadOnlyList<PlacedText> Texts, PixelBox? Pill, double Delay);

/// <summary>
/// Where everything on an Up Next bumper goes for one item at one frame size. The design is drawn at 1280x720 and
/// scales with the frame height, so every frame size gets the same card. The info column sits on the left and the
/// artwork on the right, both centred in the space between the header and the countdown; without artwork the
/// column takes the width (up to a readable measure). The title takes up to two lines and drops to a smaller size
/// when it does not fit on one at full size; every other line takes one, ending in an ellipsis when it runs out of
/// room.
/// </summary>
internal sealed class BumperLayout
{
    /// <summary>How far, in design pixels, each row slides in from.</summary>
    public const double SlideDistance = 120;

    /// <summary>The seconds each row takes to slide in.</summary>
    public const double SlideSeconds = 1.0;

    /// <summary>The accent colour: the season and episode tag and the progress bar.</summary>
    public const string Accent = "4fd8ff";

    private const string Ellipsis = "…";

    // The title's size, in design pixels, once it no longer fits on one line.
    private const int SmallTitle = 46;
    private static readonly double[] RowDelays = [1.0, 1.3, 1.6, 1.9];

    private BumperLayout()
    {
    }

    /// <summary>Gets the frame width.</summary>
    public int Width { get; private init; }

    /// <summary>Gets the frame height.</summary>
    public int Height { get; private init; }

    /// <summary>Gets the scale from the 720-line design to this frame.</summary>
    public double Scale { get; private init; }

    /// <summary>Gets the artwork frame's shape.</summary>
    public ArtShape Shape { get; private init; }

    /// <summary>Gets the artwork frame, or <c>null</c> without artwork.</summary>
    public PixelBox? Art { get; private init; }

    /// <summary>Gets the artwork frame's corner radius in pixels.</summary>
    public int ArtRadius { get; private init; }

    /// <summary>Gets the info column's rows, top to bottom.</summary>
    public IReadOnlyList<PlacedRow> Rows { get; private init; } = Array.Empty<PlacedRow>();

    /// <summary>Gets the season and episode tag pill's corner radius in pixels.</summary>
    public int PillRadius { get; private init; }

    /// <summary>Gets the parental rating badge, or <c>null</c> for an unrated item.</summary>
    public PixelBox? Badge { get; private init; }

    /// <summary>Gets the badge's text, or <c>null</c>.</summary>
    public PlacedText? BadgeText { get; private init; }

    /// <summary>Gets the badge's border width in pixels.</summary>
    public int BadgeBorder { get; private init; }

    /// <summary>Gets the badge's corner radius in pixels.</summary>
    public int BadgeRadius { get; private init; }

    /// <summary>Gets the countdown's label ("Starting in"), placed left of the number.</summary>
    public PlacedText CountdownLabel { get; private init; }

    /// <summary>Gets the countdown number's style and baseline; its <see cref="PlacedText.X"/> is the right edge it is aligned to.</summary>
    public PlacedText CountdownNumber { get; private init; }

    /// <summary>Gets the progress bar's height in pixels.</summary>
    public int BarHeight { get; private init; }

    /// <summary>Gets how far each row slides in from, in pixels.</summary>
    public int Slide => Px(SlideDistance);

    /// <summary>
    /// Lays out an item's card.
    /// </summary>
    /// <param name="content">What the card says.</param>
    /// <param name="shape">The artwork frame's shape (<see cref="ArtShape.None"/> without artwork).</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="fonts">The fonts, for measuring.</param>
    /// <returns>The layout.</returns>
    public static BumperLayout Compute(BumperContent content, ArtShape shape, int width, int height, BumperFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fonts);
        var s = height / 720.0;
        int P(double design) => (int)Math.Round(design * s);

        // The space between the header and the countdown, inset from the sides.
        var areaTop = P(132);
        var areaBottom = height - P(96);
        var left = P(72);
        var right = width - P(72);

        PixelBox? art = null;
        var columnRight = right;
        if (shape != ArtShape.None)
        {
            var (artW, artH) = shape switch
            {
                ArtShape.Portrait => (316, 474),
                ArtShape.Square => (420, 420),
                _ => (560, 315),
            };
            var w = P(artW);
            var h = P(artH);
            art = new PixelBox(right - w, areaTop + ((areaBottom - areaTop - h) / 2), w, h);
            columnRight = right - w - P(64);
        }
        else
        {
            columnRight = Math.Min(right, left + P(900));
        }

        var column = Math.Max(P(120), columnRight - left);

        // The title: the largest size from full down to the small size that fits it on one line, otherwise the
        // small size over up to two lines.
        var fullSize = shape is ArtShape.Portrait or ArtShape.None ? 64 : 58;
        var titleMetrics = fonts.For(BumperFace.DisplayBold, content.Title);
        var titleSize = P(SmallTitle);
        for (var design = fullSize; design >= SmallTitle; design -= 2)
        {
            if (titleMetrics.Metrics.Measure(content.Title, P(design)) <= column)
            {
                titleSize = P(design);
                break;
            }
        }

        var titleLines = Wrap(content.Title, titleMetrics.Metrics, titleSize, column, 2);

        // Each row: (texts measured at x = 0, pill, line height). Placed vertically once all heights are known.
        var rows = new List<(List<(string Text, BumperFace Face, int Size, double X, string Color)> Texts, (double X, double W, double H)? Pill, double Height)>();

        var titleRow = titleLines.Select(l => (l, BumperFace.DisplayBold, titleSize, 0.0, "0xFFFFFF@1")).ToList();
        rows.Add((titleRow, null, (titleSize * 1.04 * titleLines.Count) + (titleSize * 0.08)));

        if (content.Tag is { } tag)
        {
            var pillSize = P(18);
            var tagWidth = fonts.For(BumperFace.SemiBold, tag).Metrics.Measure(tag, pillSize);
            var pillW = tagWidth + P(20);
            var pillH = (pillSize * 1.2) + P(10);
            var texts = new List<(string, BumperFace, int, double, string)> { (tag, BumperFace.SemiBold, pillSize, P(10), "0x" + Accent + "@1") };
            var nameSize = P(28);
            if (content.Subtitle is { } name)
            {
                var room = column - pillW - P(14);
                var fitted = Ellipsize(name, fonts.For(BumperFace.Medium, name).Metrics, nameSize, room);
                if (fitted.Length > 0)
                {
                    texts.Add((fitted, BumperFace.Medium, nameSize, pillW + P(14), "0xFFFFFF@0.92"));
                }
            }

            rows.Add((texts, (0, pillW, pillH), Math.Max(pillH, nameSize * 1.25)));
        }
        else if (content.Subtitle is { } subtitle)
        {
            var face = content.SubtitleIsTagline ? BumperFace.MediumItalic : BumperFace.Medium;
            var size = P(content.SubtitleIsTagline ? 26 : 28);
            var fitted = Ellipsize(subtitle, fonts.For(face, subtitle).Metrics, size, column);
            rows.Add(([(fitted, face, size, 0, "0xFFFFFF@0.88")], null, size * 1.3));
        }

        if (content.Details is { } details)
        {
            var size = P(18);
            var fitted = Ellipsize(details, fonts.For(BumperFace.Regular, details).Metrics, size, column);
            rows.Add(([(fitted, BumperFace.Regular, size, 0, "0xFFFFFF@0.68")], null, size * 1.45));
        }

        if (content.Score is { } score)
        {
            var size = P(18);
            var star = fonts.Metrics(BumperFace.SemiBold).Measure("★", size);
            var value = fonts.Metrics(BumperFace.SemiBold).Measure(score, size);
            rows.Add((
                [
                    ("★", BumperFace.SemiBold, size, 0, "0xFFC94D@1"),
                    (score, BumperFace.SemiBold, size, star + P(8), "0xFFFFFF@1"),
                    ("/ 10", BumperFace.Regular, size, star + P(8) + value + P(8), "0xFFFFFF@0.6"),
                ],
                null,
                size * 1.45));
        }

        // Centre the column in the area, then stack the rows down it.
        var gap = P(14);
        var block = rows.Sum(r => r.Height) + (gap * (rows.Count - 1));
        var y = areaTop + ((areaBottom - areaTop - block) / 2);
        var placed = new List<PlacedRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (texts, pill, rowHeight) = rows[i];
            var placedTexts = new List<PlacedText>();
            if (i == 0)
            {
                // The title's lines stack at its line height.
                var lineHeight = titleSize * 1.04;
                for (var line = 0; line < texts.Count; line++)
                {
                    var (text, face, size, _, color) = texts[line];
                    var font = fonts.For(face, text);
                    placedTexts.Add(new PlacedText(text, face, font.Path, size, left, Baseline(y + (line * lineHeight), lineHeight, font.Metrics, size), color));
                }
            }
            else
            {
                foreach (var (text, face, size, x, color) in texts)
                {
                    var font = fonts.For(face, text);
                    placedTexts.Add(new PlacedText(text, face, font.Path, size, left + (int)Math.Round(x), Baseline(y, rowHeight, font.Metrics, size), color));
                }
            }

            PixelBox? pillBox = pill is { } p
                ? new PixelBox(left + (int)Math.Round(p.X), (int)Math.Round(y + ((rowHeight - p.H) / 2)), (int)Math.Round(p.W), (int)Math.Round(p.H))
                : null;
            placed.Add(new PlacedRow(placedTexts, pillBox, RowDelays[Math.Min(i, RowDelays.Length - 1)]));
            y += rowHeight + gap;
        }

        // The parental rating badge, top right.
        PixelBox? badge = null;
        PlacedText? badgeText = null;
        var border = Math.Max(1, P(2));
        if (content.Rating is { } rating)
        {
            var size = P(30);
            var font = fonts.For(BumperFace.DisplayExtraBold, rating);
            var textWidth = font.Metrics.Measure(rating, size);
            var badgeH = P(52);
            var badgeW = (int)Math.Round(textWidth) + (2 * P(12)) + (2 * border);
            badge = new PixelBox(width - P(36) - badgeW, P(36), badgeW, badgeH);
            badgeText = new PlacedText(rating, BumperFace.DisplayExtraBold, font.Path, size, badge.Value.X + border + P(12), Baseline(badge.Value.Y, badgeH, font.Metrics, size), "0xFFFFFF@1");
        }

        // The countdown, bottom right: the number right-aligned, the label to its left at a fixed distance, so the
        // label never shifts as the number shrinks from two digits to one.
        var countSize = P(18);
        var countHeight = countSize * 1.4;
        var countTop = height - P(34) - countHeight;
        var numberFont = fonts.For(BumperFace.DisplayBold, "15");
        var labelFont = fonts.For(BumperFace.Medium, "Starting in");
        var numberRoom = numberFont.Metrics.Measure("15", countSize);
        var labelWidth = labelFont.Metrics.Measure("Starting in", countSize);
        var space = labelFont.Metrics.Measure(" ", countSize);
        var number = new PlacedText("15", BumperFace.DisplayBold, numberFont.Path, countSize, right, Baseline(countTop, countHeight, numberFont.Metrics, countSize), "0xFFFFFF@1");
        var label = new PlacedText("Starting in", BumperFace.Medium, labelFont.Path, countSize, (int)Math.Round(right - numberRoom - (2 * space) - labelWidth), Baseline(countTop, countHeight, labelFont.Metrics, countSize), "0xFFFFFF@0.7");

        return new BumperLayout
        {
            Width = width,
            Height = height,
            Scale = s,
            Shape = shape,
            Art = art,
            ArtRadius = P(14),
            Rows = placed,
            PillRadius = P(6),
            Badge = badge,
            BadgeText = badgeText,
            BadgeBorder = border,
            BadgeRadius = P(8),
            CountdownLabel = label,
            CountdownNumber = number,
            BarHeight = Math.Max(2, P(4)),
        };
    }

    /// <summary>The artwork frame for an image of the given size: wide, tall or square by its proportions.</summary>
    /// <param name="width">The image width.</param>
    /// <param name="height">The image height.</param>
    /// <returns>The frame shape, or <see cref="ArtShape.None"/> for an unusable size.</returns>
    public static ArtShape ShapeFor(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return ArtShape.None;
        }

        var aspect = (double)width / height;
        return aspect >= 1.25 ? ArtShape.Landscape : aspect <= 0.8 ? ArtShape.Portrait : ArtShape.Square;
    }

    /// <summary>
    /// Breaks text into at most <paramref name="maxLines"/> lines no wider than <paramref name="width"/>, at word
    /// boundaries (a single word too long for a line is broken where it overflows), ending the last line in an
    /// ellipsis when the text does not fit.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="metrics">The font's metrics.</param>
    /// <param name="size">The font size in pixels.</param>
    /// <param name="width">The room per line in pixels.</param>
    /// <param name="maxLines">The most lines allowed.</param>
    /// <returns>The lines (at least one).</returns>
    internal static List<string> Wrap(string text, FontMetrics metrics, double size, double width, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        var index = 0;
        while (index < words.Length && lines.Count < maxLines)
        {
            var word = words[index];
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (metrics.Measure(candidate, size) <= width)
            {
                current = candidate;
                index++;
                continue;
            }

            if (current.Length == 0)
            {
                // One word wider than the line: break it where it overflows.
                var (head, tail) = SplitToFit(word, metrics, size, width);
                lines.Add(head);
                words[index] = tail;
                continue;
            }

            lines.Add(current);
            current = string.Empty;
        }

        if (current.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(current);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        if (index < words.Length)
        {
            // Text left over: the last line carries an ellipsis, cut back to a word boundary where one is near.
            var rest = lines[^1] + " " + string.Join(' ', words.Skip(index));
            lines[^1] = Ellipsize(rest, metrics, size, width, mustCut: true);
        }

        return lines;
    }

    /// <summary>
    /// The text, cut to fit <paramref name="width"/> with an ellipsis when it is too wide: at the last word boundary
    /// that fits when it keeps most of the line, otherwise wherever it overflows.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="metrics">The font's metrics.</param>
    /// <param name="size">The font size in pixels.</param>
    /// <param name="width">The room in pixels.</param>
    /// <param name="mustCut">Whether the text is known to continue past this line, so it ends in an ellipsis even when what is left fits.</param>
    /// <returns>The fitted text (empty when not even an ellipsis fits).</returns>
    internal static string Ellipsize(string text, FontMetrics metrics, double size, double width, bool mustCut = false)
    {
        if (!mustCut && metrics.Measure(text, size) <= width)
        {
            return text;
        }

        var room = width - metrics.Measure(Ellipsis, size);
        if (room <= 0)
        {
            return string.Empty;
        }

        var runes = text.EnumerateRunes().ToList();
        var fit = 0;
        double used = 0;
        foreach (var rune in runes)
        {
            var w = metrics.Measure(rune.ToString(), size);
            if (used + w > room)
            {
                break;
            }

            used += w;
            fit++;
        }

        var head = string.Concat(runes.Take(fit).Select(r => r.ToString()));
        var space = head.LastIndexOf(' ');
        if (space > 0 && space >= head.Length * 0.6)
        {
            head = head[..space];
        }

        return head.TrimEnd(' ', ',', '·', '-', ':') + Ellipsis;
    }

    // The baseline that centres a font's ascent-to-descent span in a line box.
    private static int Baseline(double top, double lineHeight, FontMetrics metrics, double size)
    {
        var ascent = metrics.Ascent(size);
        var descent = metrics.Descent(size);
        return (int)Math.Round(top + ((lineHeight - (ascent + descent)) / 2) + ascent);
    }

    // Splits a word at the most characters that fit the width (at least one, so the wrap always moves on).
    private static (string Head, string Tail) SplitToFit(string word, FontMetrics metrics, double size, double width)
    {
        var runes = word.EnumerateRunes().ToList();
        var count = 1;
        while (count < runes.Count && metrics.Measure(string.Concat(runes.Take(count + 1).Select(r => r.ToString())), size) <= width)
        {
            count++;
        }

        return (string.Concat(runes.Take(count).Select(r => r.ToString())), string.Concat(runes.Skip(count).Select(r => r.ToString())));
    }

    private int Px(double design) => (int)Math.Round(design * Scale);
}
