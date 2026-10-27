using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the 12.4.1.2 performance pass: nothing is probed with a separate process (image sizes and a rendered
/// card's end are read in process), Jellyfin's encoding settings drive the encoder (preset, threads, deinterlacer,
/// Intel low-power), renders read each image once instead of on every frame, and the next item is prepared at the
/// right moments.
/// </summary>
public sealed class PerformancePassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lc-perf-" + Guid.NewGuid().ToString("N")[..8]);

    public PerformancePassTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("png", 1920, 1080)]
    [InlineData("jpg", 1000, 1500)]
    [InlineData("gif", 320, 240)]
    [InlineData("webp", 800, 800)]
    public void ImageSizes_AreReadFromTheHeader(string format, int width, int height)
    {
        if (FfmpegHarness.Instance is not { } ffmpeg || (format == "webp" && !ffmpeg.HasEncoder("libwebp")))
        {
            return;
        }

        var path = Path.Combine(_dir, "image." + format);
        ffmpeg.Exec("-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=" + width + "x" + height + ":d=1", "-frames:v", "1", path);
        Assert.Equal((width, height), ImageSize.Read(path));
    }

    [Fact]
    public void ImageSizes_RejectWhatTheyCannotRead()
    {
        Assert.Null(ImageSize.Parse(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(ImageSize.Parse(ReadOnlySpan<byte>.Empty));
        Assert.Null(ImageSize.Read(Path.Combine(_dir, "missing.png")));

        // A PNG header that declares a zero width is not an image size.
        var png = new byte[24];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }.CopyTo(png, 0);
        png[23] = 10;
        Assert.Null(ImageSize.Parse(png));
        png[19] = 16;
        Assert.Equal((16, 10), ImageSize.Parse(png));
    }

    [Fact]
    public void ARenderedCardsEnd_IsReadFromItsOwnPackets()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var ts = Path.Combine(_dir, "clip.ts");
        ffmpeg.Exec("-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=2", "-f", "lavfi", "-i", "sine=d=2:sample_rate=48000",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", "-muxdelay", "0", "-muxpreload", "0", "-f", "mpegts", ts);

        var end = IntroService.ScanEndSeconds(ts);
        Assert.NotNull(end);
        Assert.InRange(end!.Value, 1.95, 2.15);
    }

    [Fact]
    public void TheSoftwareEncoder_FollowsJellyfinsPresetThreadsAndDeinterlacer()
    {
        var options = new EncodingOptions { EncoderPreset = EncoderPreset.faster, EncodingThreadCount = 3, DeinterlaceMethod = DeinterlaceMethod.bwdif };
        var video = Resolver(options).ResolveVideo(Models.VideoCodec.H264, allowHardware: true);

        Assert.Equal("libx264", video.Name);
        Assert.Equal("faster", video.Preset);
        Assert.Equal("bwdif", video.Deinterlacer);
        Assert.Equal(["-threads", "3"], video.ExtraEncoderArgs);

        var args = StreamArguments.Build("/m.mkv", TimeSpan.Zero, TimeSpan.Zero, 1280, 4000, video, "aac", 128, null);
        Assert.Equal("faster", args[args.IndexOf("-preset") + 1]);
        Assert.StartsWith("bwdif=deint=1,", args[args.IndexOf("-vf") + 1], StringComparison.Ordinal);
        Assert.Equal("3", args[args.IndexOf("-threads") + 1]);
    }

    [Fact]
    public void JellyfinsAutoDefaults_KeepTheRealtimeChoices()
    {
        var video = Resolver(new EncodingOptions()).ResolveVideo(Models.VideoCodec.H264, allowHardware: true);
        Assert.Equal("veryfast", video.Preset);
        Assert.Equal("yadif", video.Deinterlacer);
        Assert.Empty(video.ExtraEncoderArgs);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.qsv, false, true, "h264_qsv", true)]
    [InlineData(HardwareAccelerationType.qsv, false, false, "h264_qsv", false)]
    [InlineData(HardwareAccelerationType.vaapi, false, true, "h264_vaapi", true)]
    [InlineData(HardwareAccelerationType.qsv, true, false, "hevc_qsv", false)] // the H.264 switch does not apply to HEVC
    public void IntelLowPower_FollowsJellyfinsSwitchForTheCodec(HardwareAccelerationType accel, bool hevc, bool h264LowPower, string encoder, bool expected)
    {
        var options = new EncodingOptions
        {
            EnableHardwareEncoding = true,
            HardwareAccelerationType = accel,
            VaapiDevice = "/dev/dri/renderD128",
            EnableIntelLowPowerH264HwEncoder = h264LowPower,
        };
        var video = Resolver(options).ResolveVideo(hevc ? Models.VideoCodec.Hevc : Models.VideoCodec.H264, allowHardware: true);

        Assert.Equal(encoder, video.Name);
        Assert.Equal(expected, video.ExtraEncoderArgs.SequenceEqual(new[] { "-low_power", "1" }));
    }

    [Fact]
    public void CardRenders_ReadEachImageOnce_AndHoldIt()
    {
        var strips = new LineStrips(Path.Combine(_dir, "s"), 1280, 720);
        var card = IntroService.BuildSourceArguments(null, strips, "/tmp/card.mp4", null);
        Assert.DoesNotContain("-loop", card);
        var filter = card[card.IndexOf("-filter_complex") + 1];
        Assert.Contains("loop=loop=-1:size=1:start=0", filter, StringComparison.Ordinal);
        Assert.Contains("gradients=s=320x180:", string.Join(" ", card), StringComparison.Ordinal); // drawn small, scaled up

        var fonts = BumperFonts.Extract(Path.Combine(_dir, "fonts"), null)!;
        var layer = BumperArguments.BuildLayerArguments(strips, "/tmp/scrim.png", null, fonts.For(BumperFace.DisplaySemiBold, "Up Next").Path, "/tmp/caption.txt", 1, "/tmp/layer.mp4");
        Assert.DoesNotContain("-loop", layer);
        Assert.Contains("trim=duration=10", layer[layer.IndexOf("-filter_complex") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnimatedLogo_StillLoops_AStillOneIsHeld()
    {
        var gif = Path.Combine(_dir, "logo.gif");
        File.WriteAllBytes(gif, "GIF89a"u8.ToArray().Concat(new byte[32]).ToArray());
        var png = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(png, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[32]).ToArray());

        var animated = IntroService.LogoInput(gif, 15)!.Value;
        Assert.False(animated.Still);
        Assert.Contains("-stream_loop", animated.Args);

        var still = IntroService.LogoInput(png, 15)!.Value;
        Assert.True(still.Still);
        Assert.Equal(["-f", "image2", "-i", png], still.Args);
        Assert.Null(IntroService.LogoInput(Path.Combine(_dir, "missing.png"), 15));
    }

    [Theory]
    [InlineData(3600, 3540)]
    [InlineData(90, 30)]
    [InlineData(45, 0)]
    public void TheNextItemsStorage_IsWokenAMinuteBeforeTheBoundary(double remainingSeconds, double expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), StreamSessionService.WakeDelayFor(TimeSpan.FromSeconds(remainingSeconds)));

    private static EncoderResolver Resolver(EncodingOptions options)
    {
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration("encoding").Returns(options);
        return new EncoderResolver(config, NullLogger<EncoderResolver>.Instance);
    }
}
