using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.LiveChannels.Services;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// The ffmpeg command lines that render the Up Next bumper, and the soft chord it shares with the tune-in card. A
/// bumper is rendered in two parts so a channel's many bumpers share the costly part: the channel layer (the
/// drifting backdrop, the flowing lines, the logo and "Up Next" header, and the chord) is rendered once per channel
/// and frame size; each item then composites its artwork, text, rating badge and countdown over that layer. Static
/// shapes (rounded masks, the shadow, the badge) are drawn once as images by a single-frame pass before either.
/// Everything is rendered to a plain intermediate video that the stream later encodes like any library item.
/// </summary>
internal static class BumperArguments
{
    /// <summary>The frame rate bumpers are rendered at, matching the channel output.</summary>
    public const int Fps = StreamArguments.OutputFps;

    /// <summary>The bumper's length in seconds.</summary>
    public const double Seconds = 15.0;

    // The scene fades up from black at the start and down to black at the end, into the item.
    private const double SceneFadeIn = 0.5;
    private const double SceneFadeOut = 0.6;

    // When the header and badge fade in, and how long they take.
    private const double HeaderAt = 0.2;
    private const double HeaderFade = 0.8;

    // When the artwork fades in, how long it takes, and how far it slowly pushes in over the rest of the clip.
    private const double ArtAt = 0.7;
    private const double ArtFade = 0.9;
    private const double ArtPush = 0.07;

    // The soft chord: a D major chord (D3, A3, D4, F#4) on triangle waves, peaking near -26 dBFS so it sits well
    // under programme audio. Each note's gain is relative; the sum is scaled to the peak.
    private static readonly (double Frequency, double Gain)[] ChordNotes = [(146.83, 0.3), (220.0, 0.22), (293.66, 0.18), (369.99, 0.1)];
    private const double ChordPeak = 0.05;
    private const double ChordCutoff = 1400;

    /// <summary>
    /// The chord as an <c>aevalsrc</c> source of the given length, stereo at 48 kHz. It swells in over the first
    /// 15% of the clip (at least 1.2 seconds), holds for a tenth of it, then trails off to silence exactly at the
    /// end, its upper notes fading faster than its lower ones so it darkens as it goes. Exponential ramps, so the
    /// swell and the trail sound even rather than front-loaded.
    /// </summary>
    /// <param name="seconds">The clip length.</param>
    /// <returns>The lavfi source description.</returns>
    public static string ChordSource(double seconds)
    {
        var peakAt = Math.Max(1.2, seconds * 0.15);
        var holdUntil = Math.Min(seconds - 0.5, peakAt + (seconds * 0.1));
        var scale = ChordPeak / SumGains();
        var terms = new List<string>();
        for (var k = 0; k < ChordNotes.Length; k++)
        {
            var (frequency, gain) = ChordNotes[k];

            // ln(1e-4): the ramps start and end at -80 dB.
            var envelope = "if(lt(t," + F(peakAt) + "),exp(-9.2103*(1-t/" + F(peakAt) + ")),if(lt(t," + F(holdUntil) + "),1,exp(-9.2103*(1+" + F(0.4 * k) + ")*(t-" + F(holdUntil) + ")/" + F(seconds - holdUntil) + ")))";
            terms.Add(F(gain * scale) + "*" + envelope + "*asin(sin(2*PI*" + F(frequency) + "*t))*2/PI");
        }

        var expression = string.Join('+', terms);
        return "aevalsrc=exprs='" + expression + "|" + expression + "':s=48000:c=stereo:d=" + F(seconds);
    }

    /// <summary>The filter that softens the chord: a gentle low-pass.</summary>
    public static string ChordFilter => "lowpass=f=" + F(ChordCutoff);

    /// <summary>
    /// The single-frame pass that draws the channel layer's scrim: a soft dark ellipse behind the info column so
    /// text reads over the lines. Drawn small and scaled up, which is both cheap and as smooth as the falloff is.
    /// </summary>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="output">The PNG to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildScrimArguments(int width, int height, string output)
    {
        // An ellipse 70% of the width and 80% of the height wide, centred left of middle, darkest (78%) at its
        // centre and clear at 70% of its radius.
        const string Distance = "hypot((X/W-0.28)/0.7,(Y/H-0.5)/0.8)";
        var alpha = "255*0.78*clip(1-" + Distance + "/0.7,0,1)";
        return
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=320x180:r=1:d=1,format=rgba,geq=r='5':g='8':b='18':a='" + alpha + "',scale=" + width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture) + ":flags=bicubic",
            "-frames:v", "1", output,
        ];
    }

    /// <summary>
    /// Renders a channel's layer: the backdrop and lines (the tune-in card's look, the lines dimmed so text reads
    /// over them), the scrim, the header (the logo in its frame with "Up Next" beside it, or "Up Next" alone where
    /// the channel has no logo) fading in, and the chord.
    /// </summary>
    /// <param name="strips">The line strips and draw-in mask, already rendered.</param>
    /// <param name="scrim">The scrim image, already rendered.</param>
    /// <param name="logoInput">The input arguments that read the channel logo (see <see cref="IntroService.LogoInput"/>), or <c>null</c> without one.</param>
    /// <param name="captionFont">The font "Up Next" is set in.</param>
    /// <param name="captionFile">A text file holding "Up Next".</param>
    /// <param name="scale">The scale from the 720-line design to this frame.</param>
    /// <param name="output">The video to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildLayerArguments(LineStrips strips, string scrim, IReadOnlyList<string>? logoInput, string captionFont, string captionFile, double scale, string output)
    {
        ArgumentNullException.ThrowIfNull(strips);
        var fps = Fps.ToString(CultureInfo.InvariantCulture);
        var seconds = F(Seconds);
        int P(double design) => (int)Math.Round(design * scale);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var next = 0;
        args.AddRange(["-f", "lavfi", "-i", "gradients=s=" + strips.Width.ToString(CultureInfo.InvariantCulture) + "x" + strips.Height.ToString(CultureInfo.InvariantCulture) + ":" + IntroService.BackdropColorSpec + ":r=" + fps + ":d=" + seconds]);
        var backdrop = next++;
        var stripInputs = new int[strips.Strips.Length];
        for (var i = 0; i < strips.Strips.Length; i++)
        {
            args.AddRange(["-loop", "1", "-framerate", fps, "-t", seconds, "-i", strips.Strips[i]]);
            stripInputs[i] = next++;
        }

        args.AddRange(["-loop", "1", "-framerate", fps, "-t", seconds, "-i", strips.Mask]);
        var mask = next++;
        args.AddRange(["-loop", "1", "-framerate", fps, "-t", seconds, "-i", scrim]);
        var scrimInput = next++;
        var logo = -1;
        if (logoInput is not null)
        {
            args.AddRange(logoInput);
            logo = next++;
        }

        args.AddRange(["-f", "lavfi", "-i", ChordSource(Seconds)]);
        var audio = next++;

        var filter = new StringBuilder(IntroService.LinesFilter(strips, backdrop, stripInputs, mask, lineOpacity: 0.6, output: "lines"));
        filter.Append('[').Append(I(scrimInput)).Append(":v]format=rgba[scrim];[lines][scrim]overlay=format=auto[bg];");

        // The header sits at the design's 36-pixel margin: the logo framed 56 pixels tall (a 12-pixel gutter
        // inside a thin border), then "Up Next" 22 pixels to its right, centred on the frame.
        var margin = P(36);
        var captionSize = P(30);
        var caption = "drawtext=fontfile=" + StreamArguments.QuoteFilterValue(captionFont) + ":textfile=" + StreamArguments.QuoteFilterValue(captionFile)
            + ":expansion=none:fontcolor=0xFFFFFF@0.9:fontsize=" + I(captionSize);
        if (logo >= 0)
        {
            var logoHeight = P(56);
            var gutter = P(12);
            var border = Math.Max(1, P(2));
            var budget = strips.Width / 2;
            filter.Append('[').Append(I(logo)).Append(":v]format=rgba,scale=-2:").Append(I(logoHeight)).Append(":flags=lanczos,")
                .Append("pad=iw+").Append(I(2 * gutter)).Append(":ih+").Append(I(2 * gutter)).Append(':').Append(I(gutter)).Append(':').Append(I(gutter)).Append(":color=#00000000,")
                .Append("pad=iw+").Append(I(2 * border)).Append(":ih+").Append(I(2 * border)).Append(':').Append(I(border)).Append(':').Append(I(border)).Append(":color=white@0.35,")
                .Append("pad=iw+").Append(I(budget)).Append(":ih:0:0:color=#00000000,")
                .Append(caption).Append(":x=w-").Append(I(budget)).Append('+').Append(I(P(22))).Append(":y=(h-th)/2,")
                .Append("fade=t=in:st=").Append(F(HeaderAt)).Append(":d=").Append(F(HeaderFade)).Append(":alpha=1[header];")
                .Append("[bg][header]overlay=x=").Append(I(margin)).Append(":y=").Append(I(margin)).Append(":format=auto[top];");
        }
        else
        {
            // No logo: "Up Next" alone, on the line it would share with the logo's frame.
            var frameHeight = P(84);
            filter.Append("[bg]").Append(caption).Append(":x=").Append(I(margin)).Append(":y=").Append(I(margin)).Append("+(").Append(I(frameHeight)).Append("-th)/2")
                .Append(":alpha='clip((t-").Append(F(HeaderAt)).Append(")/").Append(F(HeaderFade)).Append(",0,1)'[top];");
        }

        // A whisper of temporal noise keeps the encoder from banding the dark gradient, as on the card.
        filter.Append("[top]noise=alls=3:allf=t+u,").Append(ToBt709).Append("[v];");
        filter.Append('[').Append(I(audio)).Append(":a]").Append(ChordFilter).Append("[a]");

        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]", "-t", seconds]);
        args.AddRange(Intermediate(output));
        return args;
    }

    /// <summary>
    /// The single-frame pass that draws an item's static shapes: the artwork's rounded mask and its soft shadow,
    /// the season and episode tag's pill, and the rating badge's fill and border. Each is drawn antialiased by its
    /// own pixel equation, once, so the composite pass only overlays images.
    /// </summary>
    /// <param name="layout">The item's layout.</param>
    /// <param name="files">Where each shape is written.</param>
    /// <returns>The arguments, or <c>null</c> when the item needs no shapes (no artwork, tag or badge).</returns>
    public static List<string>? BuildShapeArguments(BumperLayout layout, BumperShapeFiles files)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(files);
        var outputs = new List<(string Source, string File)>();
        if (layout.Art is { } art)
        {
            outputs.Add((Rounded(art.Width, art.Height, layout.ArtRadius, "255", "255", "255", "255", gray: true), files.ArtMask));
            var (shadowSource, _) = Shadow(art, layout);
            outputs.Add((shadowSource, files.Shadow));
        }

        if (layout.Rows.FirstOrDefaultPill() is { } pill)
        {
            outputs.Add((Rounded(pill.Width, pill.Height, layout.PillRadius, "79", "216", "255", F(255 * 0.16), gray: false), files.Pill));
        }

        if (layout.Badge is { } badge)
        {
            outputs.Add((Rounded(badge.Width, badge.Height, layout.BadgeRadius, "0", "0", "0", F(255 * 0.4), gray: false), files.BadgeFill));
            outputs.Add((Ring(badge.Width, badge.Height, layout.BadgeRadius, layout.BadgeBorder), files.BadgeRing));
        }

        if (outputs.Count == 0)
        {
            return null;
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var (source, _) in outputs)
        {
            args.AddRange(["-f", "lavfi", "-i", source]);
        }

        for (var i = 0; i < outputs.Count; i++)
        {
            args.AddRange(["-map", I(i), "-frames:v", "1", outputs[i].File]);
        }

        return args;
    }

    /// <summary>
    /// Composites an item's bumper over its channel's layer: the artwork (pushed in slowly, with rounded corners
    /// and a soft shadow) fading in, each row of the info column sliding in from the left in turn, the rating
    /// badge fading in, the countdown and the progress bar along the bottom, and the whole scene fading up from
    /// black and back down at the end. The layer's chord is carried over as is.
    /// </summary>
    /// <param name="layout">The item's layout.</param>
    /// <param name="layer">The channel's layer video.</param>
    /// <param name="artInput">The input arguments that read the artwork image (see <see cref="IntroService.LogoInput"/>), or <c>null</c> when <paramref name="layout"/> has none.</param>
    /// <param name="files">The item's shape images (from <see cref="BuildShapeArguments"/>).</param>
    /// <param name="textFile">Writes a piece of text to a file and returns its path (drawtext reads text from files, so no title ever needs escaping).</param>
    /// <param name="output">The video to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildItemArguments(BumperLayout layout, string layer, IReadOnlyList<string>? artInput, BumperShapeFiles files, Func<string, string> textFile, string output)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(textFile);
        var fps = Fps.ToString(CultureInfo.InvariantCulture);
        var seconds = F(Seconds);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", layer };
        var next = 1;
        int Image(string path)
        {
            args.AddRange(["-loop", "1", "-framerate", fps, "-t", seconds, "-i", path]);
            return next++;
        }

        var filter = new StringBuilder();
        filter.Append("[0:v]").Append(FromBt709).Append("[s0];");
        var stage = 0;
        string Stage() => "[s" + I(stage) + "]";
        void Overlay(string layerLabel, string x, string y)
        {
            filter.Append(Stage()).Append(layerLabel).Append("overlay=x=").Append(x).Append(":y=").Append(y).Append(":format=rgb");
            stage++;
            filter.Append(Stage()).Append(';');
        }

        void Draw(PlacedText text, string x, string? alpha, bool expand = false)
        {
            filter.Append(Stage()).Append("drawtext=fontfile=").Append(StreamArguments.QuoteFilterValue(text.FontPath))
                .Append(":textfile=").Append(StreamArguments.QuoteFilterValue(textFile(text.Text)))
                .Append(":expansion=").Append(expand ? "normal" : "none")
                .Append(":fontsize=").Append(I(text.Size)).Append(":fontcolor=").Append(text.Color)
                .Append(":x=").Append(x).Append(":y=").Append(I(text.Baseline)).Append("-ascent");
            if (alpha is not null)
            {
                filter.Append(":alpha=").Append(alpha);
            }

            stage++;
            filter.Append(Stage()).Append(';');
        }

        if (layout.Art is { } art && artInput is not null)
        {
            var (_, shadowAt) = Shadow(art, layout);
            var shadow = Image(files.Shadow);
            filter.Append('[').Append(I(shadow)).Append(":v]format=rgba,").Append(FadeIn(ArtAt, ArtFade)).Append("[shadow];");
            Overlay("[shadow]", I(shadowAt.X), I(shadowAt.Y));

            // Cover-fit at twice the frame's size, push in from there, then scale down: the push moves by whole
            // pixels of the doubled image, which is half a pixel on screen, so the slow zoom does not step.
            args.AddRange(artInput);
            var picture = next++;
            var mask = Image(files.ArtMask);
            var w2 = I(2 * art.Width);
            var h2 = I(2 * art.Height);
            filter.Append('[').Append(I(picture)).Append(":v]scale=").Append(w2).Append(':').Append(h2).Append(":force_original_aspect_ratio=increase:flags=lanczos,crop=").Append(w2).Append(':').Append(h2)
                .Append(",scale=w='").Append(w2).Append("*(1+").Append(F(ArtPush)).Append("*clip((t-").Append(F(ArtAt)).Append(")/").Append(F(Seconds - ArtAt)).Append(",0,1))':h=-2:eval=frame:flags=bicubic")
                .Append(",crop=").Append(w2).Append(':').Append(h2).Append(",scale=").Append(I(art.Width)).Append(':').Append(I(art.Height)).Append(":flags=area,format=rgba[art0];")
                .Append('[').Append(I(mask)).Append(":v]format=gray[artmask];[art0][artmask]alphamerge,").Append(FadeIn(ArtAt, ArtFade)).Append("[art];");
            Overlay("[art]", I(art.X), I(art.Y));
        }

        var pillDone = false;
        foreach (var row in layout.Rows)
        {
            // The info column's rows slide in from the left; the Then line only fades in where it rests.
            var slide = row.Slides ? SlideX(layout.Slide, row.Delay) : string.Empty;
            var alpha = "'clip((t-" + F(row.Delay) + ")/" + F(HeaderFade) + ",0,1)'";
            if (row.Divider is { } divider)
            {
                filter.Append("color=c=white@0.14:s=").Append(I(divider.Width)).Append('x').Append(I(divider.Height)).Append(":r=").Append(fps).Append(":d=").Append(seconds)
                    .Append(",format=rgba,").Append(FadeIn(row.Delay, HeaderFade)).Append("[divider];");
                Overlay("[divider]", I(divider.X), I(divider.Y));
            }

            if (row.Pill is { } pill && !pillDone)
            {
                var pillInput = Image(files.Pill);
                filter.Append('[').Append(I(pillInput)).Append(":v]format=rgba,").Append(FadeIn(row.Delay, HeaderFade)).Append("[pill];");
                Overlay("[pill]", "'" + I(pill.X) + slide + "'", I(pill.Y));
                pillDone = true;
            }

            foreach (var text in row.Texts)
            {
                Draw(text, "'" + I(text.X) + slide + "'", alpha);
            }
        }

        if (layout.Badge is { } badge && layout.BadgeText is { } badgeText)
        {
            var fill = Image(files.BadgeFill);
            var ring = Image(files.BadgeRing);
            filter.Append('[').Append(I(fill)).Append(":v]format=rgba,").Append(FadeIn(HeaderAt, HeaderFade)).Append("[badgefill];");
            filter.Append('[').Append(I(ring)).Append(":v]format=rgba,").Append(FadeIn(HeaderAt, HeaderFade)).Append("[badgering];");
            Overlay("[badgefill]", I(badge.X), I(badge.Y));
            Overlay("[badgering]", I(badge.X), I(badge.Y));
            Draw(badgeText, I(badgeText.X), "'clip((t-" + F(HeaderAt) + ")/" + F(HeaderFade) + ",0,1)'");
        }

        // The countdown counts the whole seconds left to the item, never showing zero; the number is drawn
        // right-aligned so the label beside it stays put.
        Draw(layout.CountdownLabel, I(layout.CountdownLabel.X), null);
        Draw(layout.CountdownNumber with { Text = "%{eif:max(1,ceil(" + F(Seconds) + "-t)):d}" }, I(layout.CountdownNumber.X) + "-tw", null, expand: true);

        // The progress bar slides across the bottom edge over the whole clip.
        filter.Append("color=c=0x").Append(BumperLayout.Accent).Append(":s=").Append(I(layout.Width)).Append('x').Append(I(layout.BarHeight)).Append(":r=").Append(fps).Append(":d=").Append(seconds).Append(",format=rgba[bar];");
        Overlay("[bar]", "'-W+W*clip(t/" + F(Seconds) + ",0,1)'", I(layout.Height - layout.BarHeight));

        filter.Append(Stage()).Append("fade=t=in:st=0:d=").Append(F(SceneFadeIn)).Append(",fade=t=out:st=").Append(F(Seconds - SceneFadeOut)).Append(":d=").Append(F(SceneFadeOut)).Append(',').Append(ToBt709).Append("[v]");

        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]", "-map", "0:a", "-t", seconds]);
        args.AddRange(Intermediate(output, copyAudio: true));
        return args;
    }

    /// <summary>
    /// Renders the black filler: black picture and silence, played for whatever part of a bumper's slot has no
    /// bumper (a tune-in partway through it, or one that was not rendered in time), so the item still starts on
    /// time.
    /// </summary>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="seconds">The clip length (at least the bumper's).</param>
    /// <param name="output">The video to write.</param>
    /// <returns>The arguments.</returns>
    public static List<string> BuildBlackArguments(int width, int height, double seconds, string output)
    {
        var fps = Fps.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=" + I(width) + "x" + I(height) + ":r=" + fps + ":d=" + F(seconds),
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-filter_complex", "[0:v]" + ToBt709 + "[v]",
            "-map", "[v]", "-map", "1:a", "-t", F(seconds),
        };
        args.AddRange(Intermediate(output));
        return args;
    }

    // Converts the RGB composite to the channel's tagged bt709 limited-range 4:2:0 picture.
    private static string ToBt709 => "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p";

    // Reads a bt709 limited-range picture into RGB for compositing.
    private static string FromBt709 => "scale=in_color_matrix=bt709:in_range=tv,format=gbrp";

    // The intermediate's encode: near-lossless x264 the stream re-encodes later, keyframes every second, tagged bt709.
    private static List<string> Intermediate(string output, bool copyAudio = false)
    {
        var args = new List<string>
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", "-g", Fps.ToString(CultureInfo.InvariantCulture),
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        };
        args.AddRange(copyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return args;
    }

    // An element's slide in from the left: an ease-out over one second starting at its delay, as an offset to add
    // to its resting x.
    private static string SlideX(int distance, double delay)
        => "-" + I(distance) + "*pow(1-clip((t-" + F(delay) + ")/" + F(BumperLayout.SlideSeconds) + ",0,1),3)";

    private static string FadeIn(double at, double duration)
        => "fade=t=in:st=" + F(at) + ":d=" + F(duration) + ":alpha=1";

    // A rounded rectangle filling its canvas, antialiased over one pixel, in one colour.
    private static string Rounded(int width, int height, int radius, string r, string g, string b, string alpha, bool gray)
    {
        var coverage = RoundedCoverage(radius, 0);
        var size = "s=" + I(width) + "x" + I(height);
        return gray
            ? "color=c=black:" + size + ":r=1:d=1,format=gray,geq=lum='255*" + coverage + "'"
            : "color=c=black:" + size + ":r=1:d=1,format=rgba,geq=r='" + r + "':g='" + g + "':b='" + b + "':a='" + alpha + "*" + coverage + "'";
    }

    // A rounded rectangle's border of the given width, white, antialiased.
    private static string Ring(int width, int height, int radius, int border)
    {
        var outer = RoundedCoverage(radius, 0);
        var inner = RoundedCoverage(Math.Max(0, radius - border), border);
        return "color=c=black:s=" + I(width) + "x" + I(height) + ":r=1:d=1,format=rgba,geq=r='255':g='255':b='255':a='255*clip(" + outer + "-" + inner + ",0,1)'";
    }

    // Coverage (0..1) of a pixel by a rounded rectangle inset by the given amount from the canvas edges.
    private static string RoundedCoverage(int radius, int inset)
    {
        var r = I(Math.Max(0, radius));
        var dx = "max(abs(X+0.5-W/2)-(W/2-" + I(inset) + "-" + r + "),0)";
        var dy = "max(abs(Y+0.5-H/2)-(H/2-" + I(inset) + "-" + r + "),0)";
        var edgeX = "(W/2-" + I(inset) + "-abs(X+0.5-W/2)+0.5)";
        var edgeY = "(H/2-" + I(inset) + "-abs(Y+0.5-H/2)+0.5)";
        return "clip(min(" + r + "-hypot(" + dx + "," + dy + ")+0.5,min(" + edgeX + "," + edgeY + ")),0,1)";
    }

    // The artwork's soft shadow: a dark rounded box below the frame, blurred, on a canvas big enough to hold the
    // blur. Returns the source and where its canvas goes.
    private static (string Source, (int X, int Y) At) Shadow(PixelBox art, BumperLayout layout)
    {
        var sigma = Math.Max(2, (int)Math.Round(28 * layout.Scale));
        var margin = 3 * sigma;
        var drop = (int)Math.Round(24 * layout.Scale);
        var width = art.Width + (2 * margin);
        var height = art.Height + (2 * margin);
        var r = I(layout.ArtRadius);
        var dx = "max(abs(X+0.5-W/2)-(" + I(art.Width) + "/2-" + r + "),0)";
        var dy = "max(abs(Y+0.5-H/2)-(" + I(art.Height) + "/2-" + r + "),0)";
        var inside = "clip(" + r + "-hypot(" + dx + "," + dy + ")+0.5,0,1)";
        var source = "color=c=black:s=" + I(width) + "x" + I(height) + ":r=1:d=1,format=rgba,geq=r='0':g='0':b='0':a='255*0.6*" + inside + "',format=gbrap,gblur=sigma=" + I(sigma) + ",format=rgba";
        return (source, (art.X - margin, art.Y - margin + drop));
    }

    private static PixelBox? FirstOrDefaultPill(this IReadOnlyList<PlacedRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Pill is { } pill)
            {
                return pill;
            }
        }

        return null;
    }

    private static double SumGains()
    {
        double sum = 0;
        foreach (var (_, gain) in ChordNotes)
        {
            sum += gain;
        }

        return sum;
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Where an item's static shape images are written during a render.</summary>
/// <param name="ArtMask">The artwork's rounded mask.</param>
/// <param name="Shadow">The artwork's shadow.</param>
/// <param name="Pill">The season and episode tag's pill.</param>
/// <param name="BadgeFill">The rating badge's fill.</param>
/// <param name="BadgeRing">The rating badge's border.</param>
internal sealed record BumperShapeFiles(string ArtMask, string Shadow, string Pill, string BadgeFill, string BadgeRing)
{
    /// <summary>The shape files for a render, named under a per-render path prefix.</summary>
    /// <param name="prefix">The path prefix.</param>
    /// <returns>The files.</returns>
    public static BumperShapeFiles Under(string prefix)
        => new(prefix + ".artmask.png", prefix + ".shadow.png", prefix + ".pill.png", prefix + ".badgefill.png", prefix + ".badgering.png");
}
