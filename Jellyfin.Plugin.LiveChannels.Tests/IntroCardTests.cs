using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the tune-in card's render arguments (<see cref="IntroService"/>).
/// </summary>
public class IntroCardTests
{
    [Fact]
    public void CardCoversJellyfinsFirstThreeDeliverySegments()
    {
        // Jellyfin serves a live playlist only once it has cut three segments on the encoder's keyframes; the
        // card must contain the keyframe that closes the third one, so start-up never waits on the encoder.
        Assert.True(IntroService.IntroSeconds > 3 * StreamArguments.SegmentSeconds);
    }

    [Fact]
    public void StripsTileSeamlesslyAndScrollAtTheLinesSpeed()
    {
        var strips = new LineStrips(Path.Combine(Path.GetTempPath(), "lc-card"), 1920, 1080);
        Assert.Equal(IntroService.LineCount, strips.Strips.Length);
        for (var i = 0; i < IntroService.LineCount; i++)
        {
            var (frequency, speed) = IntroService.LineMotion(i);

            // A whole-pixel period, one period per frequency, and one period of travel per 2π radians.
            Assert.Equal((int)Math.Round(1920 / frequency), strips.Periods[i]);
            Assert.Equal(strips.Periods[i] * speed / (2 * Math.PI), strips.PixelsPerSecond[i], 6);
        }

        Assert.Equal((2 * 1920) + IntroService.DrawInEdge, strips.MaskWidth);
    }

    [Fact]
    public void StripArgumentsRenderOneFramePerStripAtNativeResolution()
    {
        var strips = new LineStrips(Path.Combine(Path.GetTempPath(), "lc-card"), 1280, 720);
        var args = IntroService.BuildStripArguments(strips);

        // One lavfi input per line plus the mask, each mapped to its own single-frame output.
        Assert.Equal(IntroService.LineCount + 1, args.Count(a => a == "lavfi"));
        Assert.Equal(IntroService.LineCount + 1, args.Count(a => a == "-frames:v"));
        Assert.Contains(strips.Mask, args);
        Assert.Contains("nullsrc=s=" + (1280 + strips.Periods[0]) + "x720:r=1:d=1,format=gray,geq=lum=", string.Join(" ", args), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceArgumentsComposeAtTheFrameSizeWithAndWithoutALogo()
    {
        var strips = new LineStrips(Path.Combine(Path.GetTempPath(), "lc-card"), 3840, 2160);
        var withLogo = IntroService.BuildSourceArguments("/nonexistent/logo.png", strips, "/tmp/out.mp4");
        var joined = string.Join(" ", withLogo);

        // A logo that does not exist is treated as no logo, and the output is always last.
        Assert.DoesNotContain("-loop 1 -framerate 30 -i /nonexistent", joined, StringComparison.Ordinal);
        Assert.Equal("/tmp/out.mp4", withLogo[^1]);
        Assert.Contains("gradients=s=3840x2160:", joined, StringComparison.Ordinal);
        Assert.Contains("crop=w=3840:h=2160:x='mod(", joined, StringComparison.Ordinal);
        Assert.Contains("[bg2]null[bg3]", joined, StringComparison.Ordinal);

        // With a logo and a font, the caption is drawn onto the logo line and fades in with it; without a font
        // the logo still renders, just uncaptioned.
        var logo = Path.GetTempFileName();
        var font = Path.GetTempFileName();
        try
        {
            var captioned = string.Join(" ", IntroService.BuildSourceArguments(logo, strips, "/tmp/out.mp4", font));
            Assert.Contains("drawtext=fontfile=", captioned, StringComparison.Ordinal);
            Assert.Contains("text='Starting Channel'", captioned, StringComparison.Ordinal);
            Assert.Contains(":alpha=1[logo];[bg2][logo]overlay=", captioned, StringComparison.Ordinal);

            var silent = string.Join(" ", IntroService.BuildSourceArguments(logo, strips, "/tmp/out.mp4", null));
            Assert.DoesNotContain("drawtext", silent, StringComparison.Ordinal);
            Assert.Contains("[bg2][logo]overlay=", silent, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logo);
            File.Delete(font);
        }
        Assert.Contains("-t " + IntroService.IntroSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), joined, StringComparison.Ordinal);
    }
}
