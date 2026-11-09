using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// The ffmpeg arguments that render a placeholder card: a clip the length of every other card that loops without a
/// visible seam, so a placeholder block airs it back to back for as long as the block lasts. Every moving part
/// repeats a whole number of times per clip (driven by the frame number, so the last frame leads exactly into the
/// first), and nothing fades in or out. Sprites and static layers are drawn once in a prepass and only moved per
/// frame.
/// </summary>
internal static class PlaceholderArguments
{
    /// <summary>The clip length in seconds, the same as the tune-in and Up Next cards.</summary>
    public const double Seconds = BumperArguments.Seconds;

    private const int Supersample = 4;

    private const int Frames = (int)(Seconds * StreamArguments.OutputFps);

    private static readonly int[] LineCycles = [1, 1, 2, 1];

    private static readonly FishSpec[] Fish =
    [
        new(0.034, 0.20, -1, 1, 0.15, 10, 1, 0.0, "#2c5f7c", 0.75, false),
        new(0.030, 0.53, -1, 1, 0.65, 8, 2, 1.1, "#2c5f7c", 0.7, false),
        new(0.066, 0.76, 1, 1, 0.05, 18, 2, 0.4, "#ff8a3d", 1.0, true),
        new(0.048, 0.17, -1, 1, 0.55, 12, 1, 2.0, "#ffd23f", 1.0, false),
        new(0.040, 0.85, 1, 2, 0.35, 10, 3, 0.9, "#3fd0c9", 1.0, false),
        new(0.032, 0.07, 1, 1, 0.80, 8, 2, 2.6, "#ff6f91", 1.0, true),
    ];

    private static readonly BubbleSpec[] Bubbles =
    [
        new(0.08, 0.020, 1, 0.00, 0.010, 2), new(0.10, 0.012, 2, 0.30, 0.008, 3), new(0.07, 0.016, 1, 0.55, 0.012, 2),
        new(0.12, 0.010, 2, 0.80, 0.006, 4), new(0.88, 0.018, 1, 0.10, 0.010, 2), new(0.91, 0.012, 2, 0.45, 0.008, 3),
        new(0.86, 0.014, 1, 0.70, 0.012, 2), new(0.30, 0.010, 1, 0.20, 0.008, 3), new(0.72, 0.011, 1, 0.60, 0.008, 2),
        new(0.52, 0.009, 2, 0.90, 0.006, 3), new(0.20, 0.013, 1, 0.38, 0.010, 2), new(0.64, 0.015, 1, 0.05, 0.010, 3),
    ];

    /// <summary>The message title a new placeholder block starts with.</summary>
    public const string DefaultTitle = RatingBlock.DefaultPlaceholderTitle;

    /// <summary>The message body a new placeholder block starts with.</summary>
    public const string DefaultBody = RatingBlock.DefaultPlaceholderBody;

    /// <summary>
    /// A message with its placeholders filled in: <c>{time}</c> becomes the local time the channel resumes and
    /// <c>{contentName}</c> the title of what it resumes with. Matching is case-insensitive.
    /// </summary>
    /// <param name="template">The configured text.</param>
    /// <param name="resumeLocal">When the channel resumes, in local time.</param>
    /// <param name="contentName">What it resumes with.</param>
    /// <param name="culture">The culture the time is written in.</param>
    /// <returns>The text to draw.</returns>
    public static string ResolveMessage(string template, DateTime resumeLocal, string contentName, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template
            .Replace("{time}", resumeLocal.ToString("t", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{contentName}", contentName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The style a block renders in: a custom block whose file is missing or not a picture or video falls back to the default look.</summary>
    /// <param name="style">The configured style.</param>
    /// <param name="mediaPath">The configured custom file.</param>
    /// <returns>The style to render.</returns>
    public static PlaceholderStyle EffectiveStyle(PlaceholderStyle style, string? mediaPath)
        => style == PlaceholderStyle.Custom && MediaKindOf(mediaPath) is null ? PlaceholderStyle.Default : style;

    /// <summary>
    /// What a custom file is, from its own leading bytes, or <c>null</c> when it is missing, unreadable, or not a
    /// PNG, JPEG, WebP, GIF, or a video in an MP4/MOV, Matroska/WebM, AVI or MPEG-TS container.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Its kind, or <c>null</c>.</returns>
    public static PlaceholderMediaKind? MediaKindOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            Span<byte> head = stackalloc byte[HeadBytes];
            int read;
            using (var file = File.OpenRead(path))
            {
                read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }

            var bytes = head[..read];
            return ConfigurationValidator.ImageContentType(bytes) switch
            {
                "image/gif" => PlaceholderMediaKind.Gif,
                null => VideoDemuxer(bytes) is null ? null : PlaceholderMediaKind.Video,
                _ => PlaceholderMediaKind.Image
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // How much of a custom file is read to tell what it is: enough for three MPEG-TS packets' sync bytes.
    private const int TsPacket = 188;
    private const int HeadBytes = (2 * TsPacket) + 1;

    // The demuxer that reads a video container named outright, from its leading bytes, or null.
    private static string? VideoDemuxer(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
        {
            return "mov";
        }

        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            return "matroska";
        }

        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' && head[8] == (byte)'A' && head[9] == (byte)'V' && head[10] == (byte)'I')
        {
            return "avi";
        }

        // MPEG-TS has no magic number, only a sync byte opening every 188-byte packet: three in a row say it is one.
        return head.Length > 2 * TsPacket && head[0] == 0x47 && head[TsPacket] == 0x47 && head[2 * TsPacket] == 0x47 ? "mpegts" : null;
    }

    private static string? VideoDemuxerOf(string path)
    {
        Span<byte> head = stackalloc byte[HeadBytes];
        using var file = File.OpenRead(path);
        return VideoDemuxer(head[..file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)]);
    }

    /// <summary>The layers a style draws once before the clip.</summary>
    /// <param name="style">The style.</param>
    /// <param name="prefix">Path prefix (directory plus a per-render token) the layers are named under.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <returns>The layers' files.</returns>
    public static PlaceholderLayers Layers(PlaceholderStyle style, string prefix, int width, int height)
        => new(style, prefix, width, height);

    /// <summary>
    /// The prepass that draws a style's static layers and sprites, one frame each, or <c>null</c> when the style
    /// needs none.
    /// </summary>
    /// <param name="layers">The layers to draw.</param>
    /// <returns>The arguments, or <c>null</c>.</returns>
    public static List<string>? BuildPrepassArguments(PlaceholderLayers layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        return layers.Style switch
        {
            PlaceholderStyle.Default => IntroService.BuildStripArguments(layers.Strips!),
            PlaceholderStyle.Ocean => OceanPrepass(layers),
            PlaceholderStyle.NightSky or PlaceholderStyle.Aurora => NightPrepass(layers),
            PlaceholderStyle.Rain => RainPrepass(layers),
            PlaceholderStyle.Embers => EmbersPrepass(layers),
            _ => null
        };
    }

    /// <summary>
    /// The clip: the style's scene with the channel logo and message centred over it (a custom file shows only the
    /// message, on a band), rendered with a silent track as a plain intermediate video for the channel encoder to take like any library item.
    /// </summary>
    /// <param name="layers">The style and its prepass layers.</param>
    /// <param name="logoPath">The channel logo, or <c>null</c>.</param>
    /// <param name="customPath">The custom image, GIF or video, used by <see cref="PlaceholderStyle.Custom"/>. A GIF or video plays from its start and repeats to fill the card, or is cut at its end, so one exactly as long as the card loops cleanly.</param>
    /// <param name="title">The message title's font and the file holding its text, or <c>null</c> for none.</param>
    /// <param name="body">The message body's font and the file holding its text, or <c>null</c> for none.</param>
    /// <param name="output">The video to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildSourceArguments(PlaceholderLayers layers, string? logoPath, string? customPath, (string Font, string File)? title, (string Font, string File)? body, string output)
        => Build(layers, logoPath, customPath, title is not null, body is not null, title, body, output);

    /// <summary>
    /// The clip's scene without its message: everything <see cref="BuildSourceArguments"/> draws, with the logo
    /// placed and any panel or band sized for the message lines asked for, but the lines themselves left off so they
    /// are drawn at playback (see <see cref="TextOverlay"/>) with the resume time and content of each airing.
    /// </summary>
    /// <param name="layers">The style and its prepass layers.</param>
    /// <param name="logoPath">The channel logo, or <c>null</c>.</param>
    /// <param name="customPath">The custom image, GIF or video, used by <see cref="PlaceholderStyle.Custom"/>.</param>
    /// <param name="hasTitle">Whether a title will be drawn.</param>
    /// <param name="hasBody">Whether a body will be drawn.</param>
    /// <param name="output">The video to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildSceneArguments(PlaceholderLayers layers, string? logoPath, string? customPath, bool hasTitle, bool hasBody, string output)
        => Build(layers, logoPath, customPath, hasTitle, hasBody, null, null, output);

    /// <summary>
    /// The message lines drawn onto a scene while it plays, placed where the scene left room for them: a chain of
    /// filters ending in a comma, for the stream's overlay hook. <c>null</c> when there is nothing to draw.
    /// </summary>
    /// <param name="style">The style the scene was rendered in (see <see cref="EffectiveStyle"/>).</param>
    /// <param name="height">The frame height.</param>
    /// <param name="logoPath">The channel logo the scene was rendered with, or <c>null</c>.</param>
    /// <param name="title">The title's font and the file holding its text, or <c>null</c>.</param>
    /// <param name="body">The body's font and the file holding its text, or <c>null</c>.</param>
    /// <param name="roomForTitle">Whether the scene left room for a title, or <c>null</c> when it is the title drawn.</param>
    /// <param name="roomForBody">Whether the scene left room for a body, or <c>null</c> when it is the body drawn. A
    /// line the scene left room for but that is not drawn keeps its room, so the others stay where the scene put them.</param>
    /// <returns>The filters, or <c>null</c>.</returns>
    public static string? TextOverlay(PlaceholderStyle style, int height, string? logoPath, (string Font, string File)? title, (string Font, string File)? body, bool? roomForTitle = null, bool? roomForBody = null)
        => TextOverlay(style, height, logoPath, title is { } t ? (t.Font, t.File, TitleSize(height)) : null, body is { } b ? (b.Font, b.File, BodySize(height)) : null, roomForTitle, roomForBody);

    /// <summary>
    /// The message lines (see the overload above), each drawn at its own size: a line too wide for the frame is
    /// drawn smaller, centred on the same place.
    /// </summary>
    /// <param name="style">The style the scene was rendered in.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="logoPath">The channel logo the scene was rendered with, or <c>null</c>.</param>
    /// <param name="title">The title's font, text file and size, or <c>null</c>.</param>
    /// <param name="body">The body's font, text file and size, or <c>null</c>.</param>
    /// <param name="roomForTitle">Whether the scene left room for a title, or <c>null</c> when it is the title drawn.</param>
    /// <param name="roomForBody">Whether the scene left room for a body, or <c>null</c> when it is the body drawn.</param>
    /// <returns>The filters, or <c>null</c>.</returns>
    public static string? TextOverlay(PlaceholderStyle style, int height, string? logoPath, (string Font, string File, int Size)? title, (string Font, string File, int Size)? body, bool? roomForTitle = null, bool? roomForBody = null)
    {
        if (title is null && body is null)
        {
            return null;
        }

        var place = Placement(height, DrawsLogo(style, logoPath), roomForTitle ?? title is not null, roomForBody ?? body is not null);
        var shadow = Math.Max(1, height / 360);
        var filter = new StringBuilder();
        void Text((string Font, string File, int Size) text, int size, string color, int top)
            => filter.Append("drawtext=fontfile=").Append(StreamArguments.QuoteFilterValue(text.Font))
                .Append(":textfile=").Append(StreamArguments.QuoteFilterValue(text.File))
                .Append(":expansion=none:fontcolor=").Append(color).Append(":fontsize=").Append(I(text.Size))
                .Append(":shadowcolor=black@0.6:shadowx=").Append(I(shadow)).Append(":shadowy=").Append(I(shadow))
                .Append(":x=(w-tw)/2:y=").Append(I(top + ((size - text.Size) / 2))).Append(',');

        if (title is { } t)
        {
            Text(t, TitleSize(height), "white", place.TitleTop);
        }

        if (body is { } b)
        {
            Text(b, BodySize(height), "white@0.82", place.BodyTop);
        }

        return filter.ToString();
    }

    /// <summary>Whether a style's scene shows the channel logo, given the logo file.</summary>
    /// <param name="style">The style.</param>
    /// <param name="logoPath">The logo, or <c>null</c>.</param>
    /// <returns>Whether the logo is drawn.</returns>
    internal static bool DrawsLogo(PlaceholderStyle style, string? logoPath)
        => style != PlaceholderStyle.Custom && IntroService.LogoInput(logoPath, null) is not null;

    private static List<string> Build(PlaceholderLayers layers, string? logoPath, string? customPath, bool hasTitle, bool hasBody, (string Font, string File)? title, (string Font, string File)? body, string output)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var filter = new StringBuilder();
        var next = 0;
        var panel = false;
        var textBand = false;

        switch (layers.Style)
        {
            case PlaceholderStyle.Static:
                StaticScene(layers, filter);
                panel = true;
                break;
            case PlaceholderStyle.Error:
                ErrorScene(layers, filter);
                panel = true;
                break;
            case PlaceholderStyle.Ocean:
                next = OceanScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.NightSky:
                next = NightScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.Aurora:
                next = AuroraScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.LavaLamp:
                next = LavaScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.Rain:
                next = RainScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.Embers:
                next = EmbersScene(layers, args, filter, next);
                break;
            case PlaceholderStyle.Custom:
                next = CustomScene(layers, args, filter, next, customPath);
                textBand = true;
                break;
            default:
                next = DefaultScene(layers, args, filter, next);
                break;
        }

        var stage = "[scene]";
        if (panel)
        {
            filter.Append(stage).Append(Panel(layers, hasTitle, hasBody)).Append("[paneled];");
            stage = "[paneled]";
        }

        if (textBand && (hasTitle || hasBody))
        {
            var band = Placement(layers.Height, false, hasTitle, hasBody);
            var top = (hasTitle ? band.TitleTop : band.BodyTop) - (layers.Height * 4 / 100);
            var bottom = (hasBody ? band.BodyTop + BodySize(layers.Height) : band.TitleTop + TitleSize(layers.Height)) + (layers.Height * 5 / 100);
            filter.Append(stage).Append("drawbox=x=0:y=").Append(I(top)).Append(":w=iw:h=").Append(I(bottom - top)).Append(":color=black@0.55:t=fill[banded];");
            stage = "[banded]";
        }

        var logoInput = DrawsLogo(layers.Style, logoPath) ? IntroService.LogoInput(logoPath, null) : null;
        var place = Placement(layers.Height, logoInput is not null, hasTitle, hasBody);
        if (logoInput is { } logoRead)
        {
            args.AddRange(logoRead.Args);
            var logo = next++;
            var logoPad = "[" + I(logo) + ":v]";
            if (logoRead.Still)
            {
                filter.Append(IntroService.Held(logo, "logoimg", null));
                logoPad = "[logoimg]";
            }

            filter.Append(logoPad).Append("format=rgba,scale=-2:").Append(I(LogoHeight(layers.Height))).Append(":flags=lanczos[logo];")
                .Append(stage).Append("[logo]overlay=x=(main_w-overlay_w)/2:y=").Append(I(place.LogoTop)).Append(":format=auto[withlogo];");
            stage = "[withlogo]";
        }

        // Layers drawn small and scaled up keep their own display aspect through scale, so a 16-pixel-wide sky
        // stretched to the frame would mark every pixel 1:20 and the channel encoder would squeeze the card into a
        // sliver. The card is always square pixels.
        filter.Append(stage).Append(TextOverlay(layers.Style, layers.Height, logoPath, title, body) ?? string.Empty).Append("setsar=1,format=yuv420p[v]");
        args.AddRange(["-f", "lavfi", "-i", Silence()]);
        filter.Append(";[").Append(I(next)).Append(":a]aformat=sample_rates=48000:channel_layouts=stereo[a]");

        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]"]);
        args.AddRange([
            "-t", F(Seconds), "-r", I(StreamArguments.OutputFps),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", "-g", I(StreamArguments.OutputFps * StreamArguments.SegmentSeconds),
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
            output
        ]);
        return args;
    }

    /// <summary>
    /// Where the logo, title and body sit, by frame height: stacked with even gaps and the stack centred on the
    /// frame (on the panel, for the styles that draw one).
    /// </summary>
    /// <param name="height">The frame height.</param>
    /// <param name="hasLogo">Whether a logo is drawn.</param>
    /// <param name="hasTitle">Whether a title is drawn.</param>
    /// <param name="hasBody">Whether a body is drawn.</param>
    /// <returns>The top of each part (unused parts are 0).</returns>
    internal static (int LogoTop, int TitleTop, int BodyTop) Placement(int height, bool hasLogo, bool hasTitle, bool hasBody)
    {
        var gap = height * 4 / 100;
        var parts = new List<int>();
        if (hasLogo)
        {
            parts.Add(LogoHeight(height));
        }

        if (hasTitle)
        {
            parts.Add(TitleSize(height));
        }

        if (hasBody)
        {
            parts.Add(BodySize(height));
        }

        var total = parts.Sum() + (Math.Max(0, parts.Count - 1) * gap);
        var y = (height - total) / 2;
        int Take(bool present, int size)
        {
            if (!present)
            {
                return 0;
            }

            var top = y;
            y += size + gap;
            return top;
        }

        var logoTop = Take(hasLogo, LogoHeight(height));
        var titleTop = Take(hasTitle, TitleSize(height));
        var bodyTop = Take(hasBody, BodySize(height));
        return (logoTop, titleTop, bodyTop);
    }

    private static int LogoHeight(int height) => height * 22 / 100;

    /// <summary>The title's size by design, for a frame height.</summary>
    /// <param name="height">The frame height.</param>
    /// <returns>The font size.</returns>
    internal static int TitleSize(int height) => height * 6 / 100;

    /// <summary>The body's size by design, for a frame height.</summary>
    /// <param name="height">The frame height.</param>
    /// <returns>The font size.</returns>
    internal static int BodySize(int height) => height * 35 / 1000;

    private static string Silence() => "anullsrc=r=48000:cl=stereo";

    private static string Panel(PlaceholderLayers layers, bool hasTitle, bool hasBody)
    {
        var w = layers.Width * 60 / 100;
        var h = layers.Height * (32 + (hasTitle ? 10 : 0) + (hasBody ? 8 : 0)) / 100;
        var x = (layers.Width - w) / 2;
        var y = (layers.Height - h) / 2;
        var border = Math.Max(2, layers.Height / 360);
        return "drawbox=x=" + I(x) + ":y=" + I(y) + ":w=" + I(w) + ":h=" + I(h) + ":color=black@0.85:t=fill,"
            + "drawbox=x=" + I(x) + ":y=" + I(y) + ":w=" + I(w) + ":h=" + I(h) + ":color=white@0.35:t=" + I(border);
    }

    // The tune-in card's look, made to loop: the gradient held still, and each line scrolling a whole number of its
    // own periods per clip instead of at its free-running speed.
    private static int DefaultScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var strips = layers.Strips!;
        var w = I(layers.Width);
        var h = I(layers.Height);
        var size = w + "x" + h;
        var fps = I(StreamArguments.OutputFps);
        args.AddRange(["-f", "lavfi", "-i", IntroService.BackdropSource(layers.Width, layers.Height, 1.0 / StreamArguments.OutputFps)]);
        filter.Append(IntroService.Held(next++, "bd", null));
        for (var i = 0; i < strips.Strips.Length; i++)
        {
            args.AddRange(["-i", strips.Strips[i]]);
            filter.Append(IntroService.Held(next++, "strip" + I(i), null));
        }

        for (var i = 0; i < strips.Strips.Length; i++)
        {
            var period = strips.Periods[i];
            filter.Append("[strip").Append(I(i)).Append("]format=gray,crop=w=").Append(w).Append(":h=").Append(h)
                .Append(":x='mod(n*").Append(I(LineCycles[i] * period)).Append('/').Append(I(Frames)).Append(',').Append(I(period)).Append(")':y=0[s").Append(I(i)).Append("];");
        }

        filter.Append("[s0][s1]blend=all_mode=screen[la];[s2][s3]blend=all_mode=screen[lb];")
            .Append("color=c=#4fd8ff:s=").Append(size).Append(":r=").Append(fps).Append("[ca];")
            .Append("color=c=#c66bff:s=").Append(size).Append(":r=").Append(fps).Append("[cb];")
            .Append("[ca][la]alphamerge[lineA];[cb][lb]alphamerge[lineB];")
            .Append("[bd][lineA]overlay=format=auto[bg1];[bg1][lineB]overlay=format=auto,noise=alls=3:allf=t+u[scene];");
        return next;
    }

    // The custom file filling the frame (cropped to cover it), read with its format named outright. A still image is
    // held; a GIF or video plays from its start and repeats for the card's length, at the output rate.
    private static int CustomScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next, string? path)
    {
        var kind = MediaKindOf(path) ?? throw new ArgumentException("The custom file is missing or not a picture or video.", nameof(path));
        var input = next++;
        var pad = "[" + I(input) + ":v]";
        switch (kind)
        {
            case PlaceholderMediaKind.Image:
                args.AddRange(["-f", "image2", "-pattern_type", "none", "-i", path!]);
                filter.Append(IntroService.Held(input, "cimg", null));
                pad = "[cimg]";
                break;
            case PlaceholderMediaKind.Gif:
                args.AddRange(["-f", "gif", "-stream_loop", "-1", "-i", path!]);
                break;
            default:
                args.AddRange(["-f", VideoDemuxerOf(path!)!, "-stream_loop", "-1", "-i", path!]);
                break;
        }

        filter.Append(pad).Append("fps=").Append(I(StreamArguments.OutputFps)).Append(",scale=").Append(I(layers.Width)).Append(':').Append(I(layers.Height))
            .Append(":force_original_aspect_ratio=increase:flags=lanczos,crop=").Append(I(layers.Width)).Append(':').Append(I(layers.Height))
            .Append(",setsar=1,format=yuv420p[scene];");
        return next;
    }

    // The sky gradient and the star fields over it (see NightPrepass), ending in the returned pad. The first field
    // is still; each of the rest twinkles a whole number of times per clip, out of step with the others.
    private static (int Next, string Stage) SkyAndStars(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        var fps = I(StreamArguments.OutputFps);
        args.AddRange(["-f", "lavfi", "-i",
            "nullsrc=s=16x" + I(Math.Max(16, h / 4)) + ":r=1:d=1,format=rgb24,geq=r='3+39*pow(Y/H,1.7)':g='5+28*pow(Y/H,1.7)':b='15+81*pow(Y/H,1.7)',scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);
        filter.Append(IntroService.Held(next++, "sky", null));
        args.AddRange(["-i", layers.StarFiles![0]]);
        filter.Append(IntroService.Held(next++, "faint", null));
        for (var i = 1; i < layers.StarFiles.Length; i++)
        {
            args.AddRange(["-i", layers.StarFiles[i]]);
            filter.Append(IntroService.Held(next++, "twinkle" + I(i), null));
        }

        var size = I(w) + "x" + I(h);
        filter.Append("color=c=#e8eeff:s=").Append(size).Append(":r=").Append(fps).Append(",split=").Append(I(layers.StarFiles.Length));
        for (var i = 0; i < layers.StarFiles.Length; i++)
        {
            filter.Append("[starcol").Append(I(i)).Append(']');
        }

        filter.Append(";[starcol0][faint]alphamerge[st0];");
        for (var i = 1; i < layers.StarFiles.Length; i++)
        {
            var (cycles, phase) = Twinkles[i - 1];
            filter.Append("[twinkle").Append(I(i)).Append("]format=gray,eq=eval=frame:brightness='-0.42*(0.5+0.5*sin(2*PI*")
                .Append(I(cycles)).Append("*t/").Append(F(Seconds)).Append('+').Append(F(phase)).Append("))'[tm").Append(I(i)).Append("];")
                .Append("[starcol").Append(I(i)).Append("][tm").Append(I(i)).Append("]alphamerge[st").Append(I(i)).Append("];");
        }

        filter.Append("[sky][st0]overlay=format=auto[n0];");
        var stage = "n0";
        for (var i = 1; i < layers.StarFiles.Length; i++)
        {
            filter.Append('[').Append(stage).Append("][st").Append(I(i)).Append("]overlay=format=auto[n").Append(I(i)).Append("];");
            stage = "n" + I(i);
        }

        return (next, stage);
    }

    // The night: the starry sky, the moon, a shooting star once per clip, and hills in silhouette in front.
    private static int NightScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        (next, var stage) = SkyAndStars(layers, args, filter, next);
        args.AddRange(["-i", layers.Moon!]);
        filter.Append(IntroService.Held(next++, "moon", null));
        args.AddRange(["-i", layers.Meteor!]);
        filter.Append(IntroService.Held(next++, "meteor", null));
        args.AddRange(["-i", layers.Hills!]);
        filter.Append(IntroService.Held(next++, "hills", null));

        var moonSize = MoonSize(h);
        filter.Append('[').Append(stage).Append("][moon]overlay=x=").Append(I((w * 80 / 100) - (moonSize / 2))).Append(":y=").Append(I((h * 20 / 100) - (moonSize / 2))).Append(":format=auto[nm];");

        var startX = w * 20 / 100;
        var startY = h * 6 / 100;
        var travel = w * 28 / 100;
        var progress = "clip((t-" + F(MeteorAt) + ")/" + F(MeteorSeconds) + ",0,1)";
        filter.Append("[meteor]format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*sin(PI*clip((T-").Append(F(MeteorAt)).Append(")/").Append(F(MeteorSeconds)).Append(",0,1))'[meteorlit];")
            .Append("[nm][meteorlit]overlay=x='").Append(I(startX)).Append('+').Append(I(travel)).Append('*').Append(progress)
            .Append("':y='").Append(I(startY)).Append('+').Append(I(travel * 4 / 10)).Append('*').Append(progress)
            .Append("':enable='between(t,").Append(F(MeteorAt)).Append(',').Append(F(MeteorAt + MeteorSeconds)).Append(")':format=auto[nmm];");
        filter.Append("[nmm][hills]overlay=format=auto,noise=alls=3:allf=t+u,format=yuv420p[scene];");
        return next;
    }

    // Northern lights: two curtains over the starry sky, each a wavy lower edge with rays fading upward. Edge and rays
    // move a whole number of times per clip; drawn small each frame and scaled up, since they are soft.
    private static int AuroraScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        (next, var stage) = SkyAndStars(layers, args, filter, next);
        args.AddRange(["-i", layers.Hills!]);
        filter.Append(IntroService.Held(next++, "hills", null));
        var aw = Math.Max(16, w / 4);
        var ah = Math.Max(16, h * 3 / 16);
        var fps = I(StreamArguments.OutputFps);
        var curtains = new (string Edge, double Fall, double Strength, string Color)[]
        {
            ("0.40+0.10*sin(2*PI*(0.8*X/W+T/10)+0.3)+0.05*sin(2*PI*(2.1*X/W-2*T/10)+1.7)", 0.30, 0.85, "#4dffa8"),
            ("0.28+0.08*sin(2*PI*(1.1*X/W-T/10)+2.4)+0.04*sin(2*PI*(2.7*X/W+3*T/10))", 0.22, 0.5, "#b47bff"),
        };
        for (var i = 0; i < curtains.Length; i++)
        {
            var (edge, fall, strength, color) = curtains[i];
            var rays = "(0.62+0.38*sin(2*PI*(9*X/W+T/10)+3*sin(2*PI*(1.3*X/W-T/10))))";
            var value = "st(0," + edge + ");st(1,Y/H);255*" + F(strength) + "*clip(sin(PI*X/W)*1.6,0,1)*if(gt(ld(1),ld(0)),exp(-pow((ld(1)-ld(0))/0.03,2)),exp(-(ld(0)-ld(1))/" + F(fall) + ")*" + rays + ")";
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(aw) + "x" + I(ah) + ":r=" + fps + ",format=gray,geq=lum='" + value + "',gblur=sigma=1.2,scale=" + I(w) + ":" + I(h * 3 / 4) + ":flags=bicubic"]);
            var input = next++;
            filter.Append("color=c=").Append(color).Append(":s=").Append(I(w)).Append('x').Append(I(h * 3 / 4)).Append(":r=").Append(fps)
                .Append("[auc").Append(I(i)).Append("];[auc").Append(I(i)).Append("][").Append(I(input)).Append(":v]alphamerge[au").Append(I(i)).Append("];")
                .Append('[').Append(stage).Append("][au").Append(I(i)).Append("]overlay=format=auto[ar").Append(I(i)).Append("];");
            stage = "ar" + I(i);
        }

        filter.Append('[').Append(stage).Append("][hills]overlay=format=auto,noise=alls=3:allf=t+u,format=yuv420p[scene];");
        return next;
    }

    // A lava lamp: a field of soft blobs (and a pool at the top and bottom) computed small each frame, each blob
    // bobbing up and down and swaying a whole number of times per clip, so blobs meet, merge and part. The field is
    // scaled up and cut at one level for crisp merging edges, over a softer glow of the same field.
    private static int LavaScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        var fps = I(StreamArguments.OutputFps);
        var sw = Math.Max(16, w / 8);
        var sh = Math.Max(16, h / 8);
        var blobs = new (double X, double Sway, int SwayCycles, double SwayPhase, double Bob, int BobCycles, double BobPhase, double Radius)[]
        {
            (0.22, 0.06, 1, 0.0, 0.38, 1, 0.0, 0.13), (0.42, 0.05, 2, 1.0, 0.40, 1, 2.1, 0.10), (0.60, 0.06, 1, 2.0, 0.36, 2, 4.0, 0.12),
            (0.80, 0.05, 1, 3.1, 0.38, 1, 1.2, 0.10), (0.33, 0.04, 1, 4.0, 0.42, 1, 3.3, 0.08), (0.70, 0.05, 2, 5.0, 0.40, 1, 5.4, 0.11),
        };
        var terms = blobs.Select(b =>
            "pow(" + F(b.Radius) + "*H,2)/(pow(X-W*(" + F(b.X) + "+" + F(b.Sway) + "*sin(2*PI*" + I(b.SwayCycles) + "*T/10+" + F(b.SwayPhase) + ")),2)"
            + "+pow(Y-H*(0.5+" + F(b.Bob) + "*sin(2*PI*" + I(b.BobCycles) + "*T/10+" + F(b.BobPhase) + ")),2)+1)")
            .Concat(["pow(0.10*H,2)/(pow(Y-1.02*H,2)+1)", "pow(0.06*H,2)/(pow(Y+0.02*H,2)+1)"]);
        var field = "255*clip(0.5*(" + string.Join('+', terms) + "),0,1)";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(sw) + "x" + I(sh) + ":r=" + fps + ",format=gray,geq=lum='" + field + "'"]);
        var input = next++;
        args.AddRange(["-f", "lavfi", "-i", Gradient(w, h, (26, 8, 38), (52, 12, 44))]);
        filter.Append(IntroService.Held(next++, "lavabg", null));
        args.AddRange(["-f", "lavfi", "-i", Gradient(w, h, (255, 70, 130), (255, 160, 60))]);
        filter.Append(IntroService.Held(next++, "lavacol", null));
        filter.Append('[').Append(I(input)).Append(":v]split[lf0][lf1];")
            .Append("[lf0]scale=").Append(I(w)).Append(':').Append(I(h)).Append(":flags=bicubic,lut=c0='255*clip((val-112)/30,0,1)'[lmask];")
            .Append("[lf1]gblur=sigma=").Append(F(Math.Max(2, sh / 22.0))).Append(",lut=c0='val*0.5',scale=").Append(I(w)).Append(':').Append(I(h)).Append(":flags=bicubic[lglow];")
            .Append("[lavacol]split[lc0][lc1];[lc0][lglow]alphamerge[glow];[lc1][lmask]alphamerge[blobs];")
            .Append("[lavabg][glow]overlay=format=auto[lv0];[lv0][blobs]overlay=format=auto,noise=alls=3:allf=t+u,format=yuv420p[scene];");
        return next;
    }

    // Rain in front of a city at night: the blurred skyline is held, and three layers of streaks (far: fine, dim and
    // slow; near: heavier, brighter and fast) each fall a whole number of frame heights per clip. Each layer is one tile
    // a frame high, stacked twice so a window sliding down it wraps without a seam.
    private static int RainScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        args.AddRange(["-i", layers.City!]);
        filter.Append(IntroService.Held(next++, "city", null));
        var stage = "city";

        for (var i = 0; i < layers.RainFiles!.Length; i++)
        {
            args.AddRange(["-i", layers.RainFiles[i]]);
            filter.Append(IntroService.Held(next++, "rain" + I(i), null));
            var (cycles, opacity) = (RainFields[i].Cycles, RainFields[i].Opacity);
            filter.Append("[rain").Append(I(i)).Append("]format=gray,split[ra").Append(I(i)).Append("][rb").Append(I(i)).Append("];")
                .Append("[ra").Append(I(i)).Append("][rb").Append(I(i)).Append("]vstack,crop=w=").Append(I(w)).Append(":h=").Append(I(h))
                .Append(":x=0:y='").Append(I(h)).Append("-mod(n*").Append(I(cycles * h)).Append('/').Append(I(Frames)).Append(',').Append(I(h)).Append(")',lut=c0='val*").Append(F(opacity)).Append("'[rm").Append(I(i)).Append("];")
                .Append("color=c=#d8e4f0:s=").Append(I(w)).Append('x').Append(I(h)).Append(":r=").Append(I(StreamArguments.OutputFps)).Append("[rc").Append(I(i)).Append("];")
                .Append("[rc").Append(I(i)).Append("][rm").Append(I(i)).Append("]alphamerge[rl").Append(I(i)).Append("];")
                .Append('[').Append(stage).Append("][rl").Append(I(i)).Append("]overlay=format=auto[rs").Append(I(i)).Append("];");
            stage = "rs" + I(i);
        }

        filter.Append('[').Append(stage).Append("]noise=alls=3:allf=t+u,format=yuv420p[scene];");
        return next;
    }

    // Embers rising from a fire below the frame, as in slow footage of a bonfire at night: a dark sky over a deep
    // red glow that slowly breathes, and sparks drifting up through it, the nearest streaked by their speed. Each
    // ember is a soft sprite carried up the frame a whole number of times per clip, swaying as it goes; they fade as
    // they climb (one still fade over the whole layer) and add to the light behind them rather than covering it.
    private static int EmbersScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        var fps = I(StreamArguments.OutputFps);
        var gw = Math.Max(16, w / 8);
        var gh = Math.Max(16, h / 8);
        // The glow breathes: drawn small each frame at a strength that swells and settles a whole number of times
        // per clip, so its colour stays deep and its black stays black.
        var glow = "(1+0.14*sin(2*PI*2*T/10)+0.07*sin(2*PI*5*T/10+1.1))*exp(-(pow((X-W*0.5)/(W*0.55),2)+pow((Y-H*1.05)/(H*0.42),2)))";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(gw) + "x" + I(gh) + ":r=" + fps + ",format=rgb24,geq=r='6+120*" + glow + "':g='4+30*" + glow + "':b='6+8*" + glow + "',scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);
        var sky = next++;
        for (var i = 0; i < layers.EmberSprites!.Length; i++)
        {
            args.AddRange(["-i", layers.EmberSprites[i]]);
            filter.Append(IntroService.Held(next++, "look" + I(i), null));
        }

        args.AddRange(["-i", layers.EmberFade!]);
        filter.Append(IntroService.Held(next++, "fade", null));

        var uses = new int[EmberLooks.Length];
        foreach (var ember in Embers)
        {
            uses[ember.Look]++;
        }

        for (var i = 0; i < EmberLooks.Length; i++)
        {
            filter.Append("[look").Append(I(i)).Append("]split=").Append(I(Math.Max(1, uses[i])));
            for (var u = 0; u < Math.Max(1, uses[i]); u++)
            {
                filter.Append("[l").Append(I(i)).Append('_').Append(I(u)).Append(']');
            }

            filter.Append(';');
        }

        filter.Append("color=c=black:s=").Append(I(w)).Append('x').Append(I(h)).Append(":r=").Append(fps).Append(",format=gbrp[e0];");
        var stage = "e0";
        var taken = new int[EmberLooks.Length];
        for (var i = 0; i < Embers.Length; i++)
        {
            var e = Embers[i];
            var (_, lookH) = EmberSize(EmberLooks[e.Look], h);
            var travel = h + (2 * lookH);
            var rise = "mod(n*" + I(e.Rise * travel) + "/" + I(Frames) + "+" + I((int)(e.Offset * travel)) + "," + I(travel) + ")";
            var x = I((int)(e.X * w)) + "-overlay_w/2+" + I((int)(e.Sway * w)) + "*sin(2*PI*(n*" + I(e.SwayCycles) + "/" + I(Frames) + "+" + F(e.Phase) + "))+" + I((int)(e.Drift * w)) + "*" + rise + "/" + I(travel);
            var y = I(h + lookH) + "-" + rise;
            filter.Append('[').Append(stage).Append("][l").Append(I(e.Look)).Append('_').Append(I(taken[e.Look]++)).Append("]overlay=x='").Append(x).Append("':y='").Append(y).Append("':format=gbrp[e").Append(I(i + 1)).Append("];");
            stage = "e" + I(i + 1);
        }

        // Blended as light, in RGB: a blend in a YUV format would screen the colour difference planes too.
        filter.Append('[').Append(stage).Append("]format=gbrp[lit];[fade]format=gbrp[fadergb];[lit][fadergb]blend=all_mode=multiply[faded];")
            .Append('[').Append(I(sky)).Append(":v]format=gbrp[breathing];")
            .Append("[breathing][faded]blend=all_mode=screen,format=yuv420p,noise=alls=3:allf=t+u[scene];");
        return next;
    }

    // The ember looks, drawn once each: a size (a fraction of the frame height, wide by tall), how bright and how
    // red, and how soft. Sparks are small glowing points; the nearest are streaked upward by their speed.
    private static readonly (double Width, double Height, double Brightness, double Warmth, double Softness)[] EmberLooks =
    [
        (0.026, 0.026, 1.00, 0.70, 0.20),
        (0.018, 0.018, 1.00, 0.85, 0.20),
        (0.014, 0.050, 1.00, 0.95, 0.18),
        (0.012, 0.036, 1.00, 0.85, 0.18),
    ];

    // Every ember: its look, where it rises (a fraction of the frame width), how many times it crosses the frame per
    // clip, where in its climb it starts, how far and how often it sways, and how far it drifts sideways on the way.
    private static readonly (int Look, double X, int Rise, double Offset, double Sway, int SwayCycles, double Phase, double Drift)[] Embers = BuildEmbers();

    private static (int Look, double X, int Rise, double Offset, double Sway, int SwayCycles, double Phase, double Drift)[] BuildEmbers()
    {
        var random = new Random(20261108);
        var embers = new List<(int, double, int, double, double, int, double, double)>();
        void Add(int count, int[] looks, int[] rises, double sway, double spread)
        {
            for (var i = 0; i < count; i++)
            {
                var x = 0.5 + ((random.NextDouble() - 0.5) * spread);
                embers.Add((looks[random.Next(looks.Length)], x, rises[random.Next(rises.Length)], random.NextDouble(), sway * (0.4 + random.NextDouble()), 1 + random.Next(3), random.NextDouble(), (random.NextDouble() - 0.5) * 0.12));
            }
        }

        Add(50, [0, 1], [1, 2], 0.025, 0.95);
        Add(12, [2, 3], [2, 3], 0.015, 0.75);
        return embers.ToArray();
    }

    private static (int Width, int Height) EmberSize((double Width, double Height, double Brightness, double Warmth, double Softness) look, int height)
        => (Math.Max(4, (int)(look.Width * height)) & ~1, Math.Max(4, (int)(look.Height * height)) & ~1);

    private static List<string> EmbersPrepass(PlaceholderLayers layers)
    {
        var w = layers.Width;
        var h = layers.Height;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var look in EmberLooks)
        {
            var (sw, sh) = EmberSize(look, h);
            var cw = sw * Supersample;
            var ch = sh * Supersample;

            // Distance from the centre, 0 there and 1 at the edge, the long axis squeezed so a streak tapers.
            var d = "hypot((X-W/2)/(W/2),(Y-H/2)/(H/2))";
            var body = "(exp(-pow(" + d + "/" + F(look.Softness) + ",2))+0.3*exp(-pow(" + d + "/0.55,2)))";
            // Its colour throughout, its light as its coverage, so overlapping embers blend softly into one another.
            var light = F(look.Brightness) + "*" + body;
            var green = I((int)(255 * (0.25 + (0.6 * look.Warmth)) / 1.1));
            var blue = I((int)(255 * (0.05 + (0.45 * look.Warmth * look.Warmth)) / 1.1));
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(cw) + "x" + I(ch) + ":r=1:d=1,format=rgba,geq=r='255':g='" + green + "':b='" + blue + "':a='255*clip(" + light + "*1.1,0,1)',scale=" + I(sw) + ":" + I(sh) + ":flags=area"]);
        }

        var fw = Math.Max(16, w / 8);
        var fh = Math.Max(16, h / 8);
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(fw) + "x" + I(fh) + ":r=1:d=1,format=rgb24,geq=r='255*clip(0.15+1.4*Y/H,0,1)':g='255*clip(0.15+1.4*Y/H,0,1)':b='255*clip(0.15+1.4*Y/H,0,1)',scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);

        for (var i = 0; i < EmberLooks.Length; i++)
        {
            args.AddRange(["-map", I(i), "-frames:v", "1", layers.EmberSprites![i]]);
        }

        args.AddRange(["-map", I(EmberLooks.Length), "-frames:v", "1", layers.EmberFade!]);
        return args;
    }

    // A full-frame vertical gradient between two colours, top to bottom, drawn tiny and scaled up.
    private static string Gradient(int width, int height, (int R, int G, int B) top, (int R, int G, int B) bottom)
    {
        string Channel(int a, int b) => I(a) + "+" + I(b - a) + "*Y/H";
        return "nullsrc=s=16x" + I(Math.Max(16, height / 4)) + ":r=1:d=1,format=rgb24,geq=r='" + Channel(top.R, bottom.R) + "':g='" + Channel(top.G, bottom.G) + "':b='" + Channel(top.B, bottom.B)
            + "',scale=" + I(width) + ":" + I(height) + ":flags=bicubic";
    }

    // A fixed hash of two values and a seed, in [0, 1), so a render always draws the same city, rain and bricks.
    private static string Hash(string a, string b, double seed)
        => "abs(mod(sin((" + a + ")*12.9898+(" + b + ")*78.233+" + F(seed) + ")*43758.5453,1))";

    private static List<string> RainPrepass(PlaceholderLayers layers)
    {
        var w = layers.Width;
        var h = layers.Height;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        var cw4 = Math.Max(16, w / 4);
        var ch4 = Math.Max(16, h / 4);
        var col = "floor(X/" + F(cw4 / 22.0) + ")";
        var top = "(" + I(ch4) + "*(1-(0.16+0.34*" + Hash(col, "1", 3.1) + ")))";
        var building = "gt(Y," + top + ")";
        var lit = "gt(" + Hash("floor(X/" + F(cw4 / 140.0) + ")", "floor(Y/" + F(ch4 / 90.0) + ")", 8.7) + ",0.86)";
        var sky = "(1-Y/" + I(ch4) + ")";
        string Channel(int skyTop, int skyBottom, int wall, int window)
            => "if(" + building + ",if(" + lit + "," + I(window) + "," + I(wall) + ")," + I(skyBottom) + "+" + I(skyTop - skyBottom) + "*" + sky + ")";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(cw4) + "x" + I(ch4) + ":r=1:d=1,format=rgb24,geq=r='" + Channel(5, 40, 9, 210) + "':g='" + Channel(8, 28, 12, 150) + "':b='" + Channel(18, 46, 20, 90) + "',"
            + "gblur=sigma=" + F(Math.Max(2.0, ch4 / 45.0)) + ",scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);

        // Each tile is a grid of cells holding at most one streak each, its column, start, length and brightness
        // set by a fixed hash of the cell. A streak may start anywhere in its cell and run on into the next, so each
        // pixel checks its own cell and the one above (the top row's from the bottom row, so the tile wraps).
        foreach (var field in RainFields)
        {
            var cw = (double)w / field.Columns;
            var ch = (double)h / field.Rows;
            var thick = Math.Max(0.8, field.Thickness * h / 1080.0);
            var cell = new StringBuilder("st(0,floor(X/" + F(cw) + "));st(5,floor(Y/" + F(ch) + "));st(6,0);");
            for (var above = 0; above <= 1; above++)
            {
                cell.Append("st(1,mod(ld(5)-").Append(I(above)).Append('+').Append(I(field.Rows)).Append(',').Append(I(field.Rows)).Append("));")
                    .Append("st(2,X-ld(0)*").Append(F(cw)).Append('-').Append(F(cw * 0.1)).Append('-').Append(F(cw * 0.8)).Append('*').Append(Hash("ld(0)", "ld(1)", field.Seed)).Append(");")
                    .Append("st(3,Y-(ld(5)-").Append(I(above)).Append(")*").Append(F(ch)).Append('-').Append(F(ch)).Append('*').Append(Hash("ld(0)", "ld(1)", field.Seed + 1)).Append(");")
                    .Append("st(4,").Append(F(ch)).Append("*(0.3+0.65*").Append(Hash("ld(0)", "ld(1)", field.Seed + 2)).Append("));")
                    .Append("st(6,max(ld(6),gt(").Append(Hash("ld(0)", "ld(1)", field.Seed + 3)).Append(',').Append(F(1 - field.Density)).Append(")*clip(1-abs(ld(2))/").Append(F(thick))
                    .Append(",0,1)*between(ld(3),0,ld(4))*pow(clip(ld(3)/ld(4),0,1),1.5)*(0.3+0.7*").Append(Hash("ld(0)", "ld(1)", field.Seed + 4)).Append(")));");
            }

            cell.Append("255*ld(6)");
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(w) + "x" + I(h) + ":r=1:d=1,format=gray,geq=lum='" + cell + "'"]);
        }

        args.AddRange(["-map", "0", "-frames:v", "1", layers.City!]);
        for (var i = 0; i < layers.RainFiles!.Length; i++)
        {
            args.AddRange(["-map", I(1 + i), "-frames:v", "1", layers.RainFiles[i]]);
        }

        return args;
    }

    // The rain's depths, far to near: finer, dimmer and slower far off, heavier and faster up close. Each falls a
    // whole number of frame heights per clip, so the clip loops.
    private static readonly (int Columns, int Rows, double Density, double Seed, double Thickness, int Cycles, double Opacity)[] RainFields =
    [
        (180, 7, 0.35, 2.3, 0.5, 4, 0.3),
        (120, 5, 0.30, 6.1, 0.8, 6, 0.45),
        (70, 3, 0.22, 9.7, 1.3, 9, 0.7),
    ];

    private static readonly (int Cycles, double Phase)[] Twinkles = [(1, 0.0), (2, 2.1), (3, 4.2)];

    private const double MeteorAt = 6.2;

    private const double MeteorSeconds = 0.9;

    private static int MoonSize(int height) => Math.Max(16, height * 22 / 100);

    private static (int Width, int Height) MeteorSize(int width) => (Math.Max(32, width * 12 / 100), Math.Max(8, width * 12 / 100 * 4 / 10));

    private static List<string> NightPrepass(PlaceholderLayers layers)
    {
        var w = layers.Width;
        var h = layers.Height;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        // Each star field scatters single points by a fixed hash of the pixel position (so a render always draws the
        // same sky) at half the frame size, or a quarter for the larger stars, then softens and scales them up.
        var fields = new (int Divisor, double Density, double Seed, int Level)[] { (2, 0.0018, 1.7, 120), (2, 0.0007, 7.3, 255), (2, 0.0006, 3.9, 255), (4, 0.0012, 5.1, 255) };
        foreach (var (divisor, density, seed, level) in fields)
        {
            var sw = Math.Max(16, w / divisor);
            var sh = Math.Max(16, h / divisor);
            var hash = "abs(mod(sin(X*12.9898+Y*78.233+" + F(seed) + ")*43758.5453,1))";
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(sw) + "x" + I(sh) + ":r=1:d=1,format=gray,geq=lum='" + I(level) + "*gt(" + hash + "," + F(1 - density) + ")*lt(Y," + I(sh * 86 / 100) + ")',"
                + "gblur=sigma=0.6,lutyuv=y='min(255,val*2.2)',scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);
        }

        var ms = MoonSize(h) * Supersample;
        var mc = ms / 2.0;
        var mr = ms * 0.22;
        var disc = "lte(hypot(X-" + F(mc) + ",Y-" + F(mc) + ")," + F(mr) + ")";
        var bite = "lte(hypot(X-" + F(mc + (mr * 0.45)) + ",Y-" + F(mc - (mr * 0.25)) + ")," + F(mr * 0.9) + ")";
        var glow = "0.3*exp(-pow(max(0,hypot(X-" + F(mc) + ",Y-" + F(mc) + ")/" + F(mr) + "-1),2)*5)";
        var moonAlpha = "255*min(1,max(" + disc + "*(1-" + bite + ")," + glow + "*(1-" + disc + "*" + bite + ")))";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(ms) + "x" + I(ms) + ":r=1:d=1,format=rgba,geq=r='255':g='244':b='214':a='" + moonAlpha + "',scale=" + I(ms / Supersample) + ":" + I(ms / Supersample) + ":flags=area"]);

        var (mw, mh) = MeteorSize(w);
        var slope = (double)mh / mw;
        var streak = "pow(X/" + I(mw) + ",2)*exp(-pow((Y-X*" + F(slope) + ")/" + F(Math.Max(1.5, h / 540.0)) + ",2))";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(mw) + "x" + I(mh) + ":r=1:d=1,format=rgba,geq=r='255':g='250':b='235':a='255*min(1," + streak + ")'"]);

        var edge = F(Math.Max(1.0, h / 540.0));
        var far = "(" + I(h) + "*(0.80+0.05*sin(2*PI*X/" + I(w) + "*0.9+0.6)+0.02*sin(2*PI*X/" + I(w) + "*3.3)))";
        var near = "(" + I(h) + "*(0.89+0.04*sin(2*PI*X/" + I(w) + "*1.4+2.2)+0.015*sin(2*PI*X/" + I(w) + "*5.2)))";
        var inFar = "clip((Y-" + far + ")/" + edge + ",0,1)";
        var inNear = "clip((Y-" + near + ")/" + edge + ",0,1)";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(w) + "x" + I(h) + ":r=1:d=1,format=rgba,geq="
            + "r='if(gt(" + inNear + ",0),6,14)':g='if(gt(" + inNear + ",0),7,14)':b='if(gt(" + inNear + ",0),16,32)':a='255*max(" + inFar + "," + inNear + ")'"]);

        var output = 0;
        foreach (var star in layers.StarFiles!)
        {
            args.AddRange(["-map", I(output++), "-frames:v", "1", star]);
        }

        args.AddRange(["-map", I(output++), "-frames:v", "1", layers.Moon!]);
        args.AddRange(["-map", I(output++), "-frames:v", "1", layers.Meteor!]);
        args.AddRange(["-map", I(output), "-frames:v", "1", layers.Hills!]);
        return args;
    }

    // Grey snow at a quarter of the frame size scaled up square, a soft bright bar rolling down twice per clip, and
    // scanlines. Each frame's snow is fresh noise, so the clip's end runs into its start like any two frames.
    private static void StaticScene(PlaceholderLayers layers, StringBuilder filter)
    {
        var w = layers.Width;
        var h = layers.Height;
        var barHeight = h * 22 / 100;
        var travel = h + barHeight;
        filter.Append("color=c=gray:s=").Append(I(Math.Max(16, w / 4))).Append('x').Append(I(Math.Max(16, h / 4)))
            .Append(":r=").Append(I(StreamArguments.OutputFps)).Append(",format=yuv420p,noise=c0s=100:c0f=t,hue=s=0,eq=contrast=1.35,")
            .Append("scale=").Append(I(w)).Append(':').Append(I(h)).Append(":flags=neighbor,format=yuv420p,")
            .Append("drawbox=x=0:y='mod(t*").Append(I(2 * travel)).Append('/').Append(F(Seconds)).Append(',').Append(I(travel)).Append(")-").Append(I(barHeight))
            .Append("':w=iw:h=").Append(I(barHeight)).Append(":color=white@0.16:t=fill,")
            .Append("drawgrid=w=iw:h=").Append(I(Math.Max(3, h / 240))).Append(":t=1:color=black@0.35[scene];");
    }

    // Broadcast colour bars, held, with a brief colour-split tear twice per clip.
    private static void ErrorScene(PlaceholderLayers layers, StringBuilder filter)
    {
        var shift = I(Math.Max(4, layers.Width / 70));
        filter.Append("smptehdbars=s=").Append(I(layers.Width)).Append('x').Append(I(layers.Height)).Append(":r=").Append(I(StreamArguments.OutputFps))
            .Append(",format=yuv420p,rgbashift=rh=").Append(shift).Append(":bh=-").Append(shift)
            .Append(":enable='between(n,96,99)+between(n,222,224)',noise=alls=4:allf=t+u[scene];");
    }

    // The water: a held gradient, light rays swaying once per clip, a sandy floor with weeds, fish crossing a whole
    // number of times per clip (the far ones darker and slower), and bubbles rising in columns.
    private static int OceanScene(PlaceholderLayers layers, List<string> args, StringBuilder filter, int next)
    {
        var w = layers.Width;
        var h = layers.Height;
        args.AddRange(["-f", "lavfi", "-i",
            "gradients=s=" + I(Math.Max(16, w / 4)) + "x" + I(Math.Max(16, h / 4)) + ":c0=#0d6b96:c1=#03203d:x0=0:y0=0:x1=0:y1=" + I(Math.Max(16, h / 4)) + ":nb_colors=2:speed=0.00001:d=" + F(1.0 / StreamArguments.OutputFps)
                + ",scale=" + I(w) + ":" + I(h) + ":flags=bicubic"]);
        filter.Append(IntroService.Held(next++, "water", null));
        args.AddRange(["-i", layers.Rays!]);
        filter.Append(IntroService.Held(next++, "raysimg", null));
        args.AddRange(["-i", layers.Floor!]);
        filter.Append(IntroService.Held(next++, "floor", null));
        args.AddRange(["-i", layers.Bubble!]);
        filter.Append(IntroService.Held(next++, "bubbleimg", null));
        for (var i = 0; i < Fish.Length; i++)
        {
            args.AddRange(["-i", layers.FishFiles![i]]);
            filter.Append(IntroService.Held(next++, "fishimg" + I(i), null));
        }

        var sway = RaySway(w);
        filter.Append("color=c=#cfefff:s=").Append(I(w + (2 * sway))).Append('x').Append(I(h)).Append(":r=").Append(I(StreamArguments.OutputFps))
            .Append("[raycol];[raycol][raysimg]alphamerge[rays];")
            .Append("[water][rays]overlay=x='-").Append(I(sway)).Append('+').Append(I(sway)).Append("*sin(2*PI*n/").Append(I(Frames)).Append(")':y=0:format=auto[st0];");
        var stage = "st0";
        var step = 0;

        string Overlay(string sprite, string x, string y)
        {
            var output = "st" + I(++step);
            filter.Append('[').Append(stage).Append("][").Append(sprite).Append("]overlay=x='").Append(x).Append("':y='").Append(y).Append("':format=auto[").Append(output).Append("];");
            stage = output;
            return output;
        }

        foreach (var (fish, i) in Fish.Select((f, i) => (f, i)).Where(p => !p.f.Near))
        {
            Overlay("fishimg" + I(i), FishX(fish, w), FishY(fish, h));
        }

        Overlay("floor", "0", "0");
        foreach (var (fish, i) in Fish.Select((f, i) => (f, i)).Where(p => p.f.Near))
        {
            Overlay("fishimg" + I(i), FishX(fish, w), FishY(fish, h));
        }

        filter.Append("[bubbleimg]split=").Append(I(Bubbles.Length));
        for (var i = 0; i < Bubbles.Length; i++)
        {
            filter.Append("[bsrc").Append(I(i)).Append(']');
        }

        filter.Append(';');
        for (var i = 0; i < Bubbles.Length; i++)
        {
            var bubble = Bubbles[i];
            var size = BubbleSize(bubble, h);
            filter.Append("[bsrc").Append(I(i)).Append("]scale=").Append(I(size)).Append(':').Append(I(size)).Append(":flags=area[b").Append(I(i)).Append("];");
            var rise = h + size;
            var x = I((int)(bubble.X * w)) + "+" + I((int)(bubble.Wobble * w)) + "*sin(2*PI*(n*" + I(bubble.WobbleCycles) + "/" + I(Frames) + "+" + F(bubble.Offset) + "))";
            var y = I(h) + "-mod(n*" + I(bubble.Cycles * rise) + "/" + I(Frames) + "+" + I((int)(bubble.Offset * rise)) + "," + I(rise) + ")";
            Overlay("b" + I(i), x, y);
        }

        filter.Append('[').Append(stage).Append("]noise=alls=3:allf=t+u,format=yuv420p[scene];");
        return next;
    }

    private static int RaySway(int width) => width * 4 / 100;

    private static int BubbleSize(BubbleSpec bubble, int height) => Math.Max(6, (int)(bubble.Size * height * 2));

    private static (int Width, int Height) FishSize(FishSpec fish, int height)
    {
        var h = Math.Max(8, (int)(fish.Size * height));
        return (h * 2, h);
    }

    private static string FishX(FishSpec fish, int width)
    {
        var travel = width + (int)(width * 0.2);
        var offset = (int)(fish.Offset * travel);
        var along = "mod(n*" + I(fish.Cycles * travel) + "/" + I(Frames) + "+" + I(offset) + "," + I(travel) + ")";
        return fish.Direction > 0 ? along + "-overlay_w" : I(width) + "-" + along;
    }

    private static string FishY(FishSpec fish, int height)
        => I((int)(fish.Y * height)) + "+" + I((int)(fish.Bob * height / 1080.0)) + "*sin(2*PI*(n*" + I(fish.BobCycles) + "/" + I(Frames) + ")+" + F(fish.Phase) + ")";

    private static List<string> OceanPrepass(PlaceholderLayers layers)
    {
        var w = layers.Width;
        var h = layers.Height;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        var sway = RaySway(w);
        var rw = Math.Max(16, (w + (2 * sway)) / 4);
        var rh = Math.Max(16, h / 4);
        var rays = "255*0.22*pow(max(0,sin(2*PI*(X/" + I(rw) + "*3.2+Y/" + I(rh) + "*0.35))),6)*pow(max(0,1-Y/(" + I(rh) + "*0.8)),1.6)";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(rw) + "x" + I(rh) + ":r=1:d=1,format=gray,geq=lum='" + rays + "',gblur=sigma=2,scale=" + I(w + (2 * sway)) + ":" + I(h) + ":flags=bicubic"]);

        var surface = "(" + I(h) + "*(0.92+0.025*sin(2*PI*X/" + I(w) + "*1.3)+0.012*sin(2*PI*X/" + I(w) + "*4.1)))";
        var edge = F(Math.Max(1.0, h / 540.0));
        var sand = "clip((Y-" + surface + ")/" + edge + ",0,1)";
        var weeds = new[] { (0.06, 0.62), (0.09, 0.70), (0.17, 0.74), (0.81, 0.66), (0.86, 0.72), (0.94, 0.64), (0.40, 0.84), (0.63, 0.82) };
        var blades = weeds.Select(p =>
        {
            var top = I((int)(p.Item2 * h));
            var x = "(" + I((int)(p.Item1 * w)) + "+" + I(Math.Max(2, w / 90)) + "*sin(Y/" + I(Math.Max(4, h / 14)) + "))";
            var half = I(Math.Max(2, w / 160)) + "*(0.4+(Y-" + top + ")/(" + I(h) + "-" + top + "))";
            return "clip((" + half + "-abs(X-" + x + "))/" + edge + ",0,1)*clip((Y-" + top + ")/" + edge + ",0,1)";
        }).ToList();
        var isWeed = blades.Skip(1).Aggregate(blades[0], (acc, blade) => "max(" + acc + "," + blade + ")");
        var tone = "(1-0.35*(Y-" + I((int)(h * 0.9)) + ")/" + I(Math.Max(1, h / 10)) + ")";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(w) + "x" + I(h) + ":r=1:d=1,format=rgba,geq="
            + "r='if(gt(" + sand + ",0),88*" + tone + ",30)':g='if(gt(" + sand + ",0),104*" + tone + ",92)':b='if(gt(" + sand + ",0),110*" + tone + ",76)':a='255*max(" + sand + "," + isWeed + ")'"]);

        var bs = 160;
        var c = bs / 2.0;
        var r = bs * 0.44;
        var d = "hypot(X-" + F(c) + ",Y-" + F(c) + ")";
        var hi = "hypot(X-" + F(c - (r * 0.42)) + ",Y-" + F(c - (r * 0.42)) + ")";
        var bubbleAlpha = "255*min(1,0.85*clip(1-abs(" + d + "-" + F(r * 0.93) + ")/" + F(bs * 0.035) + ",0,1)+0.12*lt(" + d + "," + F(r) + ")+0.9*clip(1-" + hi + "/" + F(r * 0.2) + ",0,1))";
        args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(bs) + "x" + I(bs) + ":r=1:d=1,format=rgba,geq=r='205':g='240':b='255':a='" + bubbleAlpha + "'"]);

        foreach (var fish in Fish)
        {
            var (sw, sh) = FishSize(fish, h);
            var cw = sw * Supersample;
            var ch = sh * Supersample;
            var u = "(X/" + I(cw) + ")";
            var v = "(Y/" + I(ch) + ")";
            var body = "lte(pow((" + u + "-0.58)/0.38,2)+pow((" + v + "-0.5)/0.36,2),1)";
            var tail = "gte(" + u + ",0.03)*lte(" + u + ",0.3)*lte(abs(" + v + "-0.5),0.4*(0.3-" + u + ")/0.27)";
            var fin = "gte(" + u + ",0.45)*lte(" + u + ",0.66)*lte(" + v + ",0.2)*gte(" + v + ",0.2-0.18*(" + u + "-0.45)/0.21+0.0)";
            var eye = "lte(hypot((" + u + "-0.82)*" + I(cw) + ",(" + v + "-0.42)*" + I(ch) + ")," + F(ch * 0.07) + ")";
            var stripe = fish.Stripes ? "lt(abs(" + u + "-0.62),0.045)+lt(abs(" + u + "-0.40),0.035)" : "0";
            var shade = "(1.12-0.45*" + v + ")";
            var (cr, cg, cb) = Rgb(fish.Color);
            var near = fish.Near;
            string Channel(int value)
                => "if(" + eye + "*" + (near ? "1" : "0") + ",15,if(gt(" + stripe + ",0)*" + body + ",240,min(255," + I(value) + "*" + shade + ")))";
            var alpha = I((int)(255 * fish.Opacity)) + "*gt(" + body + "+" + tail + "+" + fin + ",0)";
            args.AddRange(["-f", "lavfi", "-i", "nullsrc=s=" + I(cw) + "x" + I(ch) + ":r=1:d=1,format=rgba,geq=r='" + Channel(cr) + "':g='" + Channel(cg) + "':b='" + Channel(cb) + "':a='" + alpha + "',"
                + (fish.Direction < 0 ? "hflip," : string.Empty) + "scale=" + I(sw) + ":" + I(sh) + ":flags=area"]);
        }

        args.AddRange(["-map", "0", "-frames:v", "1", layers.Rays!]);
        args.AddRange(["-map", "1", "-frames:v", "1", layers.Floor!]);
        args.AddRange(["-map", "2", "-frames:v", "1", layers.Bubble!]);
        for (var i = 0; i < Fish.Length; i++)
        {
            args.AddRange(["-map", I(3 + i), "-frames:v", "1", layers.FishFiles![i]]);
        }

        return args;
    }

    private static (int R, int G, int B) Rgb(string hex)
        => (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private readonly record struct FishSpec(double Size, double Y, int Direction, int Cycles, double Offset, int Bob, int BobCycles, double Phase, string Color, double Opacity, bool Stripes)
    {
        public bool Near => Opacity >= 1;
    }

    private readonly record struct BubbleSpec(double X, double Size, int Cycles, double Offset, double Wobble, int WobbleCycles);

    /// <summary>The files a placeholder render's prepass writes, by style.</summary>
    internal sealed class PlaceholderLayers
    {
        /// <summary>Initializes a new instance of the <see cref="PlaceholderLayers"/> class.</summary>
        /// <param name="style">The style.</param>
        /// <param name="prefix">Path prefix (directory plus a per-render token).</param>
        /// <param name="width">The frame width.</param>
        /// <param name="height">The frame height.</param>
        public PlaceholderLayers(PlaceholderStyle style, string prefix, int width, int height)
        {
            Style = style;
            Width = width;
            Height = height;
            if (style == PlaceholderStyle.Default)
            {
                Strips = new LineStrips(prefix, width, height);
            }
            else if (style == PlaceholderStyle.Ocean)
            {
                Rays = prefix + ".rays.png";
                Floor = prefix + ".floor.png";
                Bubble = prefix + ".bubble.png";
                FishFiles = Enumerable.Range(0, Fish.Length).Select(i => prefix + ".fish" + I(i) + ".png").ToArray();
            }
            else if (style == PlaceholderStyle.Rain)
            {
                City = prefix + ".city.png";
                RainFiles = Enumerable.Range(0, RainFields.Length).Select(i => prefix + ".rain" + I(i) + ".png").ToArray();
            }
            else if (style == PlaceholderStyle.Embers)
            {
                EmberSprites = Enumerable.Range(0, EmberLooks.Length).Select(i => prefix + ".ember" + I(i) + ".png").ToArray();
                EmberFade = prefix + ".emberfade.png";
            }
            else if (style is PlaceholderStyle.NightSky or PlaceholderStyle.Aurora)
            {
                StarFiles = Enumerable.Range(0, 1 + Twinkles.Length).Select(i => prefix + ".stars" + I(i) + ".png").ToArray();
                Moon = prefix + ".moon.png";
                Meteor = prefix + ".meteor.png";
                Hills = prefix + ".hills.png";
            }
        }

        /// <summary>Gets the style.</summary>
        public PlaceholderStyle Style { get; }

        /// <summary>Gets the frame width.</summary>
        public int Width { get; }

        /// <summary>Gets the frame height.</summary>
        public int Height { get; }

        /// <summary>Gets the line strips, for <see cref="PlaceholderStyle.Default"/>.</summary>
        public LineStrips? Strips { get; }

        /// <summary>Gets the light-ray mask, for <see cref="PlaceholderStyle.Ocean"/>.</summary>
        public string? Rays { get; }

        /// <summary>Gets the sea floor layer, for <see cref="PlaceholderStyle.Ocean"/>.</summary>
        public string? Floor { get; }

        /// <summary>Gets the bubble sprite, for <see cref="PlaceholderStyle.Ocean"/>.</summary>
        public string? Bubble { get; }

        /// <summary>Gets the fish sprites, for <see cref="PlaceholderStyle.Ocean"/>.</summary>
        public string[]? FishFiles { get; }

        /// <summary>Gets the star fields, the first still and the rest twinkling, for <see cref="PlaceholderStyle.NightSky"/>.</summary>
        public string[]? StarFiles { get; }

        /// <summary>Gets the moon sprite, for <see cref="PlaceholderStyle.NightSky"/>.</summary>
        public string? Moon { get; }

        /// <summary>Gets the shooting star sprite, for <see cref="PlaceholderStyle.NightSky"/>.</summary>
        public string? Meteor { get; }

        /// <summary>Gets the hills layer, for <see cref="PlaceholderStyle.NightSky"/>.</summary>
        public string? Hills { get; }

        /// <summary>Gets the blurred city, for <see cref="PlaceholderStyle.Rain"/>.</summary>
        public string? City { get; }

        /// <summary>Gets the far and near rain tiles, for <see cref="PlaceholderStyle.Rain"/>.</summary>
        public string[]? RainFiles { get; }

        /// <summary>Gets the ember sprites, one per look, for <see cref="PlaceholderStyle.Embers"/>.</summary>
        public string[]? EmberSprites { get; }

        /// <summary>Gets the fade the embers rise through, for <see cref="PlaceholderStyle.Embers"/>.</summary>
        public string? EmberFade { get; }

        /// <summary>Gets every file the prepass writes.</summary>
        public IEnumerable<string> Files
            => (Strips?.Files ?? Enumerable.Empty<string>())
                .Concat(new[] { Rays, Floor, Bubble, Moon, Meteor, Hills, City, EmberFade }.OfType<string>())
                .Concat(FishFiles ?? Array.Empty<string>())
                .Concat(StarFiles ?? Array.Empty<string>())
                .Concat(RainFiles ?? Array.Empty<string>())
                .Concat(EmberSprites ?? Array.Empty<string>());
    }
}
