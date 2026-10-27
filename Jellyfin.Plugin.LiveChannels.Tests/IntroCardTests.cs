using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the tune-in card's render arguments (<see cref="IntroService"/>).
/// </summary>
public class IntroCardTests
{
    [Fact]
    public void TheCardCountsDownToTheChannel_LikeAnUpNextCard()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-card-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var fonts = BumperFonts.Extract(Path.Combine(dir, "fonts"), null)!;
            var (label, number) = BumperLayout.Countdown(1920, 1080, fonts);
            var strips = new LineStrips(Path.Combine(dir, "card"), 1920, 1080);
            var args = IntroService.BuildSourceArguments(null, strips, "/tmp/out.mp4", null, (label, number, "/tmp/label.txt", "/tmp/number.txt"));
            var filter = args[args.IndexOf("-filter_complex") + 1];

            Assert.Contains("textfile='/tmp/label.txt'", filter, StringComparison.Ordinal);
            Assert.Contains("textfile='/tmp/number.txt':expansion=normal", filter, StringComparison.Ordinal);
            Assert.Contains(":x=" + number.X + "-tw:", filter, StringComparison.Ordinal);
            Assert.Equal("%{eif:max(1,ceil(10-t)):d}", BumperArguments.CountdownNumber(number, IntroService.IntroSeconds).Text);
            Assert.Equal(1920 - 108, number.X);

            // Without one, the card is as before.
            Assert.DoesNotContain("number.txt", string.Join(" ", IntroService.BuildSourceArguments(null, strips, "/tmp/out.mp4", null)), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ARenderedCard_RunsTenSeconds_AndClearingTheCacheRemovesIt()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-card-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var encoder = Substitute.For<IMediaEncoder>();
            encoder.EncoderPath.Returns(ffmpeg.Ffmpeg);
            var paths = Substitute.For<IApplicationPaths>();
            paths.CachePath.Returns(Path.Combine(dir, "cache"));
            paths.DataPath.Returns(Path.Combine(dir, "data"));
            var config = Substitute.For<IServerConfigurationManager>();
            config.GetConfiguration(Arg.Any<string>()).Returns(new EncodingOptions());
            using var intros = new IntroService(encoder, new EncoderResolver(config, NullLogger<EncoderResolver>.Instance), paths, NullLogger<IntroService>.Instance);
            var channel = new Channel { Id = "c1", Name = "Card", Number = 12 };

            await intros.EnsureAsync(channel, null);
            var card = intros.TryGetCached(channel);
            Assert.NotNull(card);
            Assert.InRange(card!.EndSeconds, 9.9, 10.2);

            // The countdown sits in the top right and changes as it counts down.
            var early = ffmpeg.Frame(card.Path, Path.Combine(dir, "early.png"), 1.0);
            var late = ffmpeg.Frame(card.Path, Path.Combine(dir, "late.png"), 8.5);
            if (Environment.GetEnvironmentVariable("LIVECHANNELS_BUMPER_PREVIEW") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                foreach (var at in new[] { 1.0, 5.0, 8.5 })
                {
                    ffmpeg.Frame(card.Path, Path.Combine(folder, "tunein-" + at.ToString("0.0", CultureInfo.InvariantCulture) + ".png"), at);
                }
            }

            Assert.True(File.Exists(early) && File.Exists(late));

            intros.ClearCache();
            Assert.Null(intros.TryGetCached(channel));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheCardIsTenSeconds_TheSameAsAnUpNextCard()
        => Assert.Equal(BumperService.BumperLength.TotalSeconds, IntroService.IntroSeconds);

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
        Assert.Contains("gradients=s=960x540:", joined, StringComparison.Ordinal);
        Assert.Contains("scale=3840:2160:flags=bicubic", joined, StringComparison.Ordinal);
        Assert.Contains("crop=w=3840:h=2160:x='mod(", joined, StringComparison.Ordinal);
        Assert.Contains("[bg2]null[bg3]", joined, StringComparison.Ordinal);

        // With a logo and a font, the caption is drawn onto the logo line and fades in with it; without a font
        // the logo still renders, just uncaptioned.
        var logo = Path.GetTempFileName();
        File.WriteAllBytes(logo, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }); // the card only draws a real image
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

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("gif")]
    public void EveryLogoType_OpensWithTheCardsInputOptions(string extension)
    {
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-logo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var logo = Path.Combine(dir, "logo." + extension);
            ff.Exec("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=256x256:d=0.1", "-frames:v", "1", logo);

            // The logo's own input options, exactly as the card render passes them, decoding one second of it.
            var args = IntroService.BuildSourceArguments(logo, new LineStrips(Path.Combine(dir, "strip"), 1280, 720), Path.Combine(dir, "out.mp4"));
            var end = args.IndexOf(logo) + 1;
            var start = args.IndexOf("-y") + 1;
            var input = args.Skip(start).Take(end - start).Concat(new[] { "-t", "1", "-f", "null", "-" }).ToList();
            var (exit, _, err) = FfmpegHarness.Run(ff.Ffmpeg, new[] { "-v", "error" }.Concat(input), timeoutSeconds: 30);
            Assert.True(exit == 0, extension + " logo failed to open: " + err);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFileThatIsNotAnImage_IsNeverHandedToFfmpegAsTheLogo()
    {
        // A logo stored before logos were checked could be anything; one that is not a real image is left off the
        // card rather than given to ffmpeg to guess at (a playlist ffmpeg would fetch, for one).
        var logo = Path.GetTempFileName();
        File.WriteAllText(logo, "#EXTM3U\nhttp://127.0.0.1:9/x.ts\n");
        try
        {
            var joined = string.Join(" ", IntroService.BuildSourceArguments(logo, new LineStrips(Path.Combine(Path.GetTempPath(), "lc-card"), 1280, 720), "/tmp/out.mp4", null));
            Assert.DoesNotContain(logo, joined, StringComparison.Ordinal);
            Assert.DoesNotContain("[logo]", joined, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logo);
        }
    }
}
