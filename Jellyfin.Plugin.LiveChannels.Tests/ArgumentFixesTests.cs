using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Producer arguments for the awkward sources: a Blu-ray PGS track on a cropped encode, a phone video held upright,
/// software HEVC with scene cuts, and the decode choices that keep those on a path ffmpeg handles correctly.
/// </summary>
public class ArgumentFixesTests : IClassFixture<MediaFixtures>
{
    private const int Width = 1280;
    private const double NoPacing = 600;

    private static readonly VideoEncoderProfile H264 =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private static readonly VideoEncoderProfile Hevc =
        new("libx265", "HEVC (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private readonly MediaFixtures _media;
    private readonly FfmpegHarness? _ff = FfmpegHarness.Instance;

    public ArgumentFixesTests(MediaFixtures media) => _media = media;

    [Theory]
    [InlineData(0)] // from the start
    [InlineData(1.5)] // a tune-in seek into the item
    public void APgsCaption_OnACroppedEncode_IsOnScreen(double seekSeconds)
    {
        if (_ff is null)
        {
            return;
        }

        // The caption sits at y 950-1010 of the disc's 1920x1080 frame, below the bottom of the 800-line picture.
        // Overlaid unscaled it fell off the picture entirely; scaled to the picture it lands near the bottom.
        var output = Produce("pgs_cropped.mkv", H264, TimeSpan.FromSeconds(seekSeconds), (0, false));
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));

        var at = Math.Max(0.5, 2.0 - seekSeconds);
        var (r, g, b) = _ff.Rgb(output, at, Width / 2, 568);
        Assert.True(r > 200 && g > 200 && b > 200, $"no caption on screen at the bottom of the picture (rgb {r},{g},{b})");

        // And nothing else of the picture changed: the top stays black.
        var (tr, tg, tb) = _ff.Rgb(output, at, Width / 2, 200);
        Assert.True(tr < 40 && tg < 40 && tb < 40, $"the caption spilled over the picture (rgb {tr},{tg},{tb})");
    }

    [Fact]
    public void APgsCaption_ClearsWhenTheTrackSaysSo_AndTheVideoKeepsGoing()
    {
        if (_ff is null)
        {
            return;
        }

        var output = Produce("pgs_cropped.mkv", H264, TimeSpan.Zero, (0, false));
        var (r, _, _) = _ff.Rgb(output, 3.5, Width / 2, 568);
        Assert.True(r < 40, $"the caption stayed after it was cleared (r {r})");
        Assert.True(_ff.Packets(output, "v:0").Max(p => p.Pts + p.Duration) > 3.8, "the video stopped with the subtitle track");
    }

    [Fact]
    public void APhoneVideoHeldUpright_PlaysUpright_WhenDecodedInSoftware()
    {
        if (_ff is null)
        {
            return;
        }

        // 1280x720 coded, rotated 90 degrees for display: upright it is taller than wide, pillarboxed in the frame.
        var output = Produce("rotated_phone.mp4", H264, TimeSpan.Zero, null);
        var (w, h) = _ff.ActiveArea(output);
        Assert.True(h > w, $"the picture is {w}x{h}: sideways");
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(270, 270)]
    [InlineData(180, 180)]
    [InlineData(-180, 180)]
    [InlineData(360, 0)]
    [InlineData(0, 0)]
    [InlineData(null, 0)]
    public void Rotation_IsNormalisedToQuarterTurns(int? probed, int expected)
        => Assert.Equal(expected, ChannelService.NormalizeRotation(probed));

    [Fact]
    public void RotatedItems_AndSdPictureSubtitles_DecodeInSoftware()
    {
        ProgramEntry Item(int height, int rotation = 0) => new(Guid.NewGuid(), "x", null, 1, "/x.mkv") { SourceHeight = height, SourceRotation = rotation };

        Assert.True(StreamSessionService.MustDecodeInSoftware(Item(1080, rotation: 90), null));
        Assert.True(StreamSessionService.MustDecodeInSoftware(Item(480), (0, false))); // a DVD's VOBSUB
        Assert.False(StreamSessionService.MustDecodeInSoftware(Item(480), (0, true))); // text renders on any path
        Assert.False(StreamSessionService.MustDecodeInSoftware(Item(1080), (0, false))); // a Blu-ray's PGS fits the GPU path
        Assert.False(StreamSessionService.MustDecodeInSoftware(Item(1080), null));
    }

    [Fact]
    public void RotatedItems_StreamItemByItem()
    {
        ProgramEntry Item(int rotation) => new(Guid.NewGuid(), "x", null, 1, "/x.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, SourceRotation = rotation };
        Assert.True(StreamSessionService.ConcatCompatible(new[] { Item(0), Item(0) }));
        Assert.False(StreamSessionService.ConcatCompatible(new[] { Item(0), Item(90) }));
    }

    [Fact]
    public void SoftwareHevc_KeepsAKeyframeEveryThreeSeconds_ThroughSceneCuts()
    {
        if (_ff is null || !_ff.HasEncoder("libx265"))
        {
            return;
        }

        var output = Produce("scene_cuts.mkv", Hevc, TimeSpan.Zero, null);
        var (_, stdout, _) = FfmpegHarness.Run(_ff.Ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", output });
        var keyframes = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains('K', StringComparison.Ordinal))
            .Select(l => double.Parse(l.Split(',')[0], CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(keyframes.Count >= 3, "too few keyframes: " + string.Join(", ", keyframes));
        for (var i = 1; i < keyframes.Count; i++)
        {
            Assert.InRange(keyframes[i] - keyframes[i - 1], 2.99, 3.01);
        }
    }

    private string Produce(string fixture, VideoEncoderProfile video, TimeSpan offset, (int, bool)? forcedSubtitle)
    {
        var args = StreamArguments.Build(_media[fixture], offset, TimeSpan.Zero, Width, 2000, video, "aac", 128, forcedSubtitle, softwareDecode: true, initialBurstSeconds: NoPacing);
        var output = _media.Output(System.IO.Path.GetFileNameWithoutExtension(fixture) + ".ts");
        _ff!.RunProducer(args, output);
        return output;
    }
}
