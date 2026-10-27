using System;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The producer's stderr tail keeps ffmpeg's log messages and drops its <c>-progress</c> lines, so a failure's
/// one explanatory error line is still there when the process exits.
/// </summary>
public class StderrTailTests
{
    [Theory]
    [InlineData("frame=120")]
    [InlineData("fps=29.97")]
    [InlineData("stream_0_0_q=23.0")]
    [InlineData("bitrate=N/A")]
    [InlineData("out_time_us=4000000")]
    [InlineData("out_time=00:00:04.000000")]
    [InlineData("speed=1.02x")]
    [InlineData("progress=continue")]
    [InlineData("progress=end")]
    [InlineData("bitrate= 676.5kbits/s")] // ffmpeg pads some values
    [InlineData("speed=   1x")]
    [InlineData("total_size=     1048576")]
    public void ProgressLines_AreRecognised(string line) => Assert.True(StreamSessionService.IsProgressLine(line));

    [Theory]
    [InlineData("[h264 @ 0x7a2117c000] co located POCs unavailable")]
    [InlineData("Error opening input file /media/missing.mkv.")]
    [InlineData("[vost#0:0/h264_qsv @ 0x55] Error while opening encoder - maybe incorrect parameters such as bit_rate")]
    [InlineData("Impossible to convert between the formats supported by the filter 'Parsed_hwupload_5' and the filter 'auto_scale_0'")]
    [InlineData("/media/a=b.mkv: No such file or directory")]
    [InlineData("")]
    [InlineData("=x")]
    [InlineData("Last message repeated 39 times")]
    [InlineData("frame dropped = 3")]
    public void Messages_AreKept(string line) => Assert.False(StreamSessionService.IsProgressLine(line));

    [Fact]
    public void RealProducer_CleanRun_PrintsOnlyProgress()
    {
        // A clean production run at -loglevel error prints nothing but progress, so every line it writes must be
        // classified as progress, or the tail would fill with noise again.
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        using var media = new MediaFixtures();
        var software = new VideoEncoderProfile("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);
        var args = StreamArguments.Build(media["video_only.mkv"], default, default, 640, 1000, software, "aac", 128, null, softwareDecode: true, initialBurstSeconds: 600);
        var stderr = ff.RunProducer(args, media.Output("clean.ts"));

        var lines = stderr.Split('\r', '\n').Where(l => l.Length > 0).ToList();
        Assert.Contains(lines, l => l.StartsWith("out_time_us=", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.True(StreamSessionService.IsProgressLine(l), "not classified as progress: " + l));
    }
}
