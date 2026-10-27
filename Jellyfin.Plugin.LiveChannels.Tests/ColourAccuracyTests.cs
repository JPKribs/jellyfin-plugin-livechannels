using System;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Every item leaves in the channel's one colour format, bt709 at limited range, tagged as such, and its colours
/// survive the trip: bars of known RGB, read back the way a player reads the stream, land within a few code
/// values of where they started whatever matrix, range, or tagging the source had.
/// </summary>
public class ColourAccuracyTests : IClassFixture<MediaFixtures>
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Tolerance = 4; // of 255: 8-bit rounding through two conversions and an encode

    private static readonly (int R, int G, int B)[] Expected =
    {
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (128, 128, 128), (220, 160, 120)
    };

    private static readonly VideoEncoderProfile Software =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private readonly MediaFixtures _media;
    private readonly FfmpegHarness? _ff = FfmpegHarness.Instance;

    public ColourAccuracyTests(MediaFixtures media) => _media = media;

    [Theory]
    [InlineData("bt709", 480, "bt709")] // a tag always wins
    [InlineData("smpte170m", 1080, "bt601")]
    [InlineData("bt470bg", 576, "bt601")]
    [InlineData("BT2020NC", 2160, "bt2020")]
    [InlineData(null, 480, "bt601")] // untagged SD: what players assume
    [InlineData("unknown", 576, "bt601")]
    [InlineData(null, 720, "bt709")] // untagged HD
    [InlineData(null, 0, "auto")] // nothing known: leave it to ffmpeg
    public void InputMatrix_FollowsTheTag_ElseWhatPlayersAssume(string? colorSpace, int height, string expected)
        => Assert.Equal(expected, StreamArguments.InputMatrix(colorSpace, height));

    [Fact]
    public void EveryPath_ConvertsAndTags()
    {
        var sdr = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, Software, "aac", 192, null, inputMatrix: "bt601");
        var vf = sdr[sdr.IndexOf("-vf") + 1];
        Assert.Contains(":in_color_matrix=bt601:out_color_matrix=bt709:out_range=tv,", vf, StringComparison.Ordinal);
        Assert.Contains("setparams=field_mode=prog:range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709", vf, StringComparison.Ordinal);
        Assert.Contains("-colorspace", sdr);
        Assert.Equal("tv", sdr[sdr.IndexOf("-color_range") + 1]);

        // HDR is scaled as-is (it is bt2020 PQ); the tone map produces the channel's bt709 itself.
        var hdr = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, Software, "aac", 192, null, isHdr: true, inputMatrix: "bt2020");
        Assert.DoesNotContain("out_color_matrix", hdr[hdr.IndexOf("-vf") + 1], StringComparison.Ordinal);

        var concat = StreamArguments.BuildConcat("/l.txt", default, 1280, 4000, Software, "aac", 192, inputMatrix: "bt601");
        Assert.Contains(concat, x => x.Contains(":in_color_matrix=bt601:out_color_matrix=bt709:out_range=tv", StringComparison.Ordinal));

        var slate = StreamArguments.BuildSlate(1280, 4000, 10, TimeSpan.Zero, null);
        Assert.Equal("bt709", slate[slate.IndexOf("-colorspace") + 1]);
    }

    [Theory]
    [InlineData("bars_hd709.mkv", "bt709", 1080)]
    [InlineData("bars_sd601.mkv", "smpte170m", 480)] // an SD DVD: bt601 pixels, converted to bt709
    [InlineData("bars_sd_untagged.mkv", null, 480)] // untagged SD: read as bt601, as players do
    [InlineData("bars_hd_untagged.mkv", null, 1080)] // untagged HD: read as bt709, as players do
    [InlineData("bars_hd_full.mkv", "bt709", 1080)] // full range: brought into limited range
    public void Sdr_ArrivesAsBt709Limited_WithItsColoursIntact(string fixture, string? colorSpace, int height)
    {
        if (_ff is null)
        {
            return;
        }

        AssertFixtureTagging(fixture, colorSpace);
        var output = Produce(fixture, StreamArguments.InputMatrix(colorSpace, height), Software, softwareDecode: true);
        AssertChannelColourFormat(output);
        AssertBars(output, fixture);
    }

    [Theory]
    [InlineData("bars_hd709.mkv", "bt709")]
    [InlineData("bars_hd_full.mkv", "bt709")] // full range must be brought down to 16-235, not passed through
    [InlineData("bars_sd601.mkv", "smpte170m")]
    public void Sdr_LimitedRangeLevels_AreTrue(string fixture, string colorSpace)
    {
        if (_ff is null)
        {
            return;
        }

        // Raw luma, read without any range conversion: limited-range white is 235 and grey 126 (128 of 255 maps to
        // 16 + 128 * 219 / 255). A full-range source passed straight through would put white at 255.
        var output = Produce(fixture, StreamArguments.InputMatrix(colorSpace, 1080), Software, softwareDecode: true);
        Assert.InRange(RawLuma(output, BarCentre(fixture, 3), Height / 2), 233, 237);
        Assert.InRange(RawLuma(output, BarCentre(fixture, 4), Height / 2), 124, 128);
    }

    [Fact]
    public void Letterbox_IsLimitedRangeBlack()
    {
        if (_ff is null)
        {
            return;
        }

        // A 4:3 source is pillarboxed; the bars must be true limited-range black (16), not full-range 0.
        var output = Produce("grey_4x3.mkv", "bt709", Software, softwareDecode: true);
        Assert.InRange(RawLuma(output, 40, Height / 2), 15, 17);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hdr_ToneMapsToBt709_KeepingHuesAndNeutrals(bool tonemapx)
    {
        if (_ff is null || (tonemapx && !_ff.HasFilter("tonemapx")))
        {
            return;
        }

        var stage = tonemapx ? EncoderResolver.ToneMapX(MediaBrowser.Model.Entities.TonemappingAlgorithm.bt2390, 0, 100, 0) : null;
        var args = StreamArguments.Build(_media["bars_hdr10.mkv"], TimeSpan.Zero, TimeSpan.Zero, Width, 4000, Software, "aac", 128, null, softwareDecode: true, isHdr: true, initialBurstSeconds: 600, toneMapStage: stage, inputMatrix: "bt2020");
        var output = _media.Output("hdr-bars.ts");
        _ff.RunProducer(args, output);

        AssertChannelColourFormat(output);

        // Tone mapping changes levels by design, so the check is on what must survive it: each primary stays the
        // dominant channel by a wide margin, greys stay grey, and white stays bright.
        var bars = Enumerable.Range(0, 6).Select(i => _ff.Rgb(output, 0.5, BarCentre("bars_hdr10.mkv", i), Height / 2)).ToArray();
        string Show() => string.Join(" ", bars.Select(c => $"({c.R},{c.G},{c.B})"));
        Assert.True(bars[0].R > bars[0].G + 100 && bars[0].R > bars[0].B + 100, "red " + Show());
        Assert.True(bars[1].G > bars[1].R + 100 && bars[1].G > bars[1].B + 100, "green " + Show());
        Assert.True(bars[2].B > bars[2].R + 100 && bars[2].B > bars[2].G + 100, "blue " + Show());
        foreach (var grey in new[] { bars[3], bars[4] })
        {
            Assert.True(Math.Abs(grey.R - grey.G) <= 8 && Math.Abs(grey.G - grey.B) <= 8, "neutral " + Show());
        }

        // Reference white (203 nits) lands well up the SDR scale, above the mid grey, and the greys keep their order.
        Assert.True(bars[3].R >= 170, "white " + Show());
        Assert.True(bars[3].R > bars[4].R + 40, "white above grey " + Show());
        Assert.True(bars[5].R > bars[5].G && bars[5].G > bars[5].B, "skin tone order " + Show());
    }

    [Fact]
    public void Concat_ConvertsSdItemsToo()
    {
        if (_ff is null)
        {
            return;
        }

        var list = _media.Output("colour-list.txt");
        System.IO.File.WriteAllLines(list, new[] { "file '" + _media["bars_sd601.mkv"] + "'" });
        var output = _media.Output("colour-concat.ts");
        _ff.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, Width, 4000, Software, "aac", 128, initialBurstSeconds: 600, inputMatrix: "bt601"), output, outputSeconds: 0.9);
        AssertChannelColourFormat(output);
        AssertBars(output, "bars_sd601.mkv", at: 0.3);
    }

    [Theory]
    [InlineData("bars_hd709.mkv", "bt709", 1080)]
    [InlineData("bars_sd601.mkv", "smpte170m", 480)]
    [InlineData("bars_hd_full.mkv", "bt709", 1080)]
    public void VideoToolbox_ArrivesAsBt709Limited_WithItsColoursIntact(string fixture, string colorSpace, int height)
    {
        if (_ff is null || !OperatingSystem.IsMacOS() || !_ff.HasEncoder("h264_videotoolbox"))
        {
            return;
        }

        var options = new EncodingOptions { EnableHardwareEncoding = true };
        var prop = typeof(EncodingOptions).GetProperty("HardwareAccelerationType")!;
        prop.SetValue(options, prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, "videotoolbox", true) : "videotoolbox");
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        var profile = new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>()).ResolveVideo(VideoCodec.H264, allowHardware: true);

        var output = Produce(fixture, StreamArguments.InputMatrix(colorSpace, height), profile, softwareDecode: false);
        AssertChannelColourFormat(output);
        AssertBars(output, fixture);
    }

    // The centre of bar i in the 1280x720 output: HD fills the frame; a 4:3 or anamorphic SD source sits inside
    // its pillarbox (720x480 at 32:27 is 16:9 and fills it too).
    private static int BarCentre(string fixture, int i) => (int)((i + 0.5) * Width / 6.0);

    private string Produce(string fixture, string inputMatrix, VideoEncoderProfile profile, bool softwareDecode)
    {
        var args = StreamArguments.Build(_media[fixture], TimeSpan.Zero, TimeSpan.Zero, Width, 8000, profile, "aac", 128, null, softwareDecode: softwareDecode, initialBurstSeconds: 600, inputMatrix: inputMatrix);
        var output = _media.Output("colour.ts");
        _ff!.RunProducer(args, output);
        return output;
    }

    private void AssertFixtureTagging(string fixture, string? colorSpace)
    {
        var tags = _ff!.Stream(_media[fixture], "video")!.Value;
        var actual = tags.TryGetProperty("color_space", out var cs) ? cs.GetString() : null;
        if (colorSpace is null)
        {
            Assert.True(actual is null or "unknown", fixture + " must be untagged for this test, but is " + actual);
        }
        else
        {
            Assert.Equal(colorSpace, actual);
        }
    }

    private void AssertChannelColourFormat(string output)
    {
        var video = _ff!.Stream(output, "video")!.Value;
        string? Tag(string name) => video.TryGetProperty(name, out var v) ? v.GetString() : null;
        Assert.Equal("yuv420p", Tag("pix_fmt"));
        Assert.Equal("tv", Tag("color_range"));
        Assert.Equal("bt709", Tag("color_space"));
        Assert.Equal("bt709", Tag("color_primaries"));
        Assert.Equal("bt709", Tag("color_transfer"));
    }

    private void AssertBars(string output, string fixture, double at = 0.5)
    {
        var bars = Enumerable.Range(0, 6).Select(i => _ff!.Rgb(output, at, BarCentre(fixture, i), Height / 2)).ToArray();
        var error = bars.Zip(Expected, (a, e) => Math.Max(Math.Abs(a.R - e.R), Math.Max(Math.Abs(a.G - e.G), Math.Abs(a.B - e.B)))).Max();
        Assert.True(error <= Tolerance, $"{fixture}: off by {error}: " + string.Join(" ", bars.Select(c => $"({c.R},{c.G},{c.B})")));
    }

    // The raw luma code value at a point (averaged over a few pixels), with no colour or range conversion.
    private int RawLuma(string output, int x, int y)
    {
        var (_, stderr) = (0, FfmpegHarness.Run(_ff!.Ffmpeg, new[]
        {
            "-hide_banner", "-i", output, "-ss", "0.5", "-frames:v", "1",
            "-vf", $"crop=8:8:{x - 4}:{y - 4},signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"
        }).StdErr);
        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"YAVG=([\d.]+)");
        Assert.True(match.Success, "no luma read: " + stderr);
        return (int)Math.Round(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
