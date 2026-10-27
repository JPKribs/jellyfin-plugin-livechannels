using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
/// End-to-end checks of the producer, concat, slate, and segmenter argument lists against a real jellyfin-ffmpeg:
/// every item must come out in the one channel format (size, square pixels, frame rate, progressive scan, SDR,
/// stereo 48 kHz), decode cleanly, land a seek on the requested frame, and chain its timestamps into the next
/// item without a jump. Skipped quietly where no jellyfin-ffmpeg is installed (see <see cref="FfmpegHarness"/>).
/// </summary>
public class StreamPipelineIntegrationTests : IClassFixture<MediaFixtures>
{
    private const int Width = 1280;
    private const int Height = 720;
    private const double NoPacing = 600; // a burst longer than any fixture, so producers run at full speed

    private static readonly VideoEncoderProfile Software =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private readonly MediaFixtures _media;
    private readonly FfmpegHarness? _ff = FfmpegHarness.Instance;

    public StreamPipelineIntegrationTests(MediaFixtures media) => _media = media;

    [Fact]
    public void Progressive_ComesOutInTheChannelFormat()
    {
        if (_ff is null)
        {
            return;
        }

        var output = Produce("progressive_1080p.mkv");

        var video = _ff.Stream(output, "video")!.Value;
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal(Width, video.GetProperty("width").GetInt32());
        Assert.Equal(Height, video.GetProperty("height").GetInt32());
        Assert.Equal("yuv420p", video.GetProperty("pix_fmt").GetString());
        Assert.Equal("30/1", video.GetProperty("r_frame_rate").GetString());
        AssertSquarePixels(video);

        var audio = _ff.Stream(output, "audio")!.Value;
        Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
        Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        Assert.Equal("48000", audio.GetProperty("sample_rate").GetString());

        var (videoEnd, _) = AssertAvEndsAligned(output);
        Assert.InRange(videoEnd, 5.9, 6.1);
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));
    }

    [Theory]
    [InlineData("grey_4x3.mkv", 960, 720)] // 4:3 pillarboxed
    [InlineData("grey_anamorphic_dvd.mkv", 1280, 720)] // 720x480 at SAR 32:27 is 16:9 and fills the frame
    [InlineData("grey_16x9.mkv", 1280, 720)] // exact fit, no sliver of padding
    [InlineData("grey_scope.mkv", 1280, 692)] // 1.85:1 letterboxed
    [InlineData("grey_sar_unknown.mkv", 1080, 720)] // unsignalled SAR is read as square pixels
    public void Geometry_KeepsTheDisplayAspectRatio(string fixture, int activeWidth, int activeHeight)
    {
        if (_ff is null)
        {
            return;
        }

        var output = Produce(fixture);

        var (w, h) = _ff.ActiveArea(output);
        Assert.InRange(w, activeWidth - 4, activeWidth + 4);
        Assert.InRange(h, activeHeight - 4, activeHeight + 4);
        var video = _ff.Stream(output, "video")!.Value;
        Assert.Equal(Width, video.GetProperty("width").GetInt32());
        Assert.Equal(Height, video.GetProperty("height").GetInt32());
        AssertSquarePixels(video);
    }

    [Fact]
    public void Concat_Anamorphic_KeepsTheDisplayAspectRatio()
    {
        if (_ff is null)
        {
            return;
        }

        var list = _media.Output("list.txt");
        File.WriteAllLines(list, new[] { "file '" + _media["grey_anamorphic_dvd.mkv"] + "'" });
        var output = _media.Output("concat-dvd.ts");
        _ff.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, Width, 2000, Software, "aac", 128, initialBurstSeconds: NoPacing), output, outputSeconds: 3);

        var (w, h) = _ff.ActiveArea(output);
        Assert.InRange(w, 1276, 1280);
        Assert.InRange(h, 716, 720);
    }

    [Fact]
    public void Interlaced_ComesOutProgressive()
    {
        if (_ff is null)
        {
            return;
        }

        Assert.True(_ff.Idet(_media["interlaced_1080i.mkv"]).Interlaced > 0, "the fixture must actually be interlaced");

        var output = Produce("interlaced_1080i.mkv");

        var (interlaced, progressive) = _ff.Idet(output);
        Assert.True(interlaced <= progressive / 10, $"output still combed: {interlaced} interlaced vs {progressive} progressive frames");
        var video = _ff.Stream(output, "video")!.Value;
        if (video.TryGetProperty("field_order", out var order))
        {
            Assert.Equal("progressive", order.GetString());
        }
    }

    [Theory]
    [InlineData(false)] // the portable zscale chain
    [InlineData(true)] // jellyfin-ffmpeg's tonemapx, with Jellyfin's default settings
    public void Hdr_IsToneMappedToSdr(bool tonemapx)
    {
        if (_ff is null || (tonemapx && !_ff.HasFilter("tonemapx")))
        {
            return;
        }

        var stage = tonemapx ? EncoderResolver.ToneMapX(MediaBrowser.Model.Entities.TonemappingAlgorithm.bt2390, 0, 100, 0) : null;
        var output = Produce("hdr10_pq.mkv", isHdr: true, toneMapStage: stage);

        var video = _ff.Stream(output, "video")!.Value;
        Assert.Equal("yuv420p", video.GetProperty("pix_fmt").GetString());
        if (video.TryGetProperty("color_transfer", out var trc))
        {
            Assert.NotEqual("smpte2084", trc.GetString());
        }

        // A missed or doubled tone map shows up as a near-black or washed-out frame.
        Assert.InRange(_ff.MeanLuma(output), 40, 200);
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));
    }

    [Fact]
    public void Surround_IsDownmixedToStereo()
    {
        if (_ff is null)
        {
            return;
        }

        Assert.Equal(6, _ff.Stream(_media["surround_51.mkv"], "audio")!.Value.GetProperty("channels").GetInt32());

        var output = Produce("surround_51.mkv");

        Assert.Equal(2, _ff.Stream(output, "audio")!.Value.GetProperty("channels").GetInt32());
    }

    [Fact]
    public void Seek_LandsOnTheRequestedFrame()
    {
        if (_ff is null)
        {
            return;
        }

        var source = _media["progressive_1080p.mkv"];
        var output = Produce("progressive_1080p.mkv", offset: TimeSpan.FromSeconds(2));

        // The first frame out must be the source frame at 2.000s (testsrc2 changes every frame), not a keyframe
        // before it or a later one.
        var scale = "scale=" + Width + ":" + Height;
        var first = _ff.Frame(output, _media.Output("first.png"));
        var target = _ff.Frame(source, _media.Output("target.png"), 2.0, scale);
        var later = _ff.Frame(source, _media.Output("later.png"), 2.5, scale);
        var atTarget = _ff.Ssim(first, target);
        Assert.True(atTarget > 0.9, $"first frame is not the 2.0s frame (ssim {atTarget:0.000})");
        Assert.True(atTarget > _ff.Ssim(first, later), "first frame matches a later frame better than the target");
    }

    [Fact]
    public void TextSubtitle_IsBurnedIntoThePicture()
    {
        if (_ff is null || !_ff.HasFilter("subtitles"))
        {
            return;
        }

        Assert.NotEmpty(_ff.Packets(_media["text_subtitle.mkv"], "s:0"));

        var plain = Produce("text_subtitle.mkv");
        var burned = Produce("text_subtitle.mkv", forcedSubtitle: (0, true));

        var a = _ff.Frame(plain, _media.Output("plain.png"), 1);
        var b = _ff.Frame(burned, _media.Output("burned.png"), 1);
        var similarity = _ff.Ssim(a, b);
        Assert.True(similarity < 0.99, $"the burned output looks identical to the plain one (ssim {similarity:0.0000})");
        Assert.Equal(string.Empty, _ff.DecodeErrors(burned));
    }

    [Fact]
    public void ConsecutiveItems_ChainTimestampsWithoutAJump()
    {
        if (_ff is null)
        {
            return;
        }

        // Item A from zero, then item B continuing at A's actual video end: exactly how the per-item producer
        // chains items. The concatenated bytes are what the segmenter receives.
        var a = Produce("progressive_1080p.mkv");
        var aVideo = _ff.Packets(a, "v:0");
        var aEnd = aVideo.Max(p => p.Pts + p.Duration);
        var b = Produce("grey_4x3.mkv", timeline: TimeSpan.FromSeconds(aEnd));

        var joined = _media.Output("joined.ts");
        using (var outStream = File.Create(joined))
        {
            foreach (var part in new[] { a, b })
            {
                using var inStream = File.OpenRead(part);
                inStream.CopyTo(outStream);
            }
        }

        var video = _ff.Packets(joined, "v:0");
        var dts = video.Select(p => p.Dts).ToList();
        for (var i = 1; i < dts.Count; i++)
        {
            Assert.True(dts[i] > dts[i - 1], $"video DTS went backwards at packet {i}: {dts[i - 1]:0.000} -> {dts[i]:0.000}");
        }

        // Ordered by presentation time, no gap between frames may exceed two frame periods (the seam included).
        var pts = video.Select(p => p.Pts).OrderBy(t => t).ToList();
        var worst = pts.Zip(pts.Skip(1), (x, y) => y - x).Max();
        Assert.True(worst < 2.0 / StreamArguments.OutputFps, $"largest frame gap {worst:0.000}s");

        // The audio runs through the seam with neither a hole nor an overlap.
        var audio = _ff.Packets(joined, "a:0").OrderBy(p => p.Pts).ToList();
        for (var i = 1; i < audio.Count; i++)
        {
            var gap = audio[i].Pts - (audio[i - 1].Pts + audio[i - 1].Duration);
            Assert.True(Math.Abs(gap) < 0.05, $"audio jumps {gap:0.000}s at {audio[i].Pts:0.000}s");
        }
    }

    [Fact]
    public void VideoOnly_GetsASilentStereoTrack()
    {
        if (_ff is null)
        {
            return;
        }

        Assert.Null(_ff.Stream(_media["video_only.mkv"], "audio"));

        var output = Produce("video_only.mkv", silentAudio: TimeSpan.FromSeconds(4));

        var audio = _ff.Stream(output, "audio")!.Value;
        Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
        Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        Assert.Equal("48000", audio.GetProperty("sample_rate").GetString());
        AssertAvEndsAligned(output);
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));
    }

    [Fact]
    public void VideoOnly_BurnInSeek_KeepsTheSilenceAlignedWithThePicture()
    {
        if (_ff is null || !_ff.HasFilter("subtitles"))
        {
            return;
        }

        // The burn-in seek runs under -copyts, where the picture keeps its source timestamps: the generated
        // silence has to start at the same point or the two tracks drift apart by the seek offset.
        var output = Produce("video_only_subtitle.mkv", offset: TimeSpan.FromSeconds(2), forcedSubtitle: (0, true), silentAudio: TimeSpan.FromSeconds(3));

        var videoStart = _ff.Packets(output, "v:0").Min(p => p.Pts);
        var audioStart = _ff.Packets(output, "a:0").Min(p => p.Pts);
        Assert.True(Math.Abs(videoStart - audioStart) < 0.1, $"video starts at {videoStart:0.000}s, audio at {audioStart:0.000}s");
        var (videoEnd, _) = AssertAvEndsAligned(output);
        Assert.InRange(videoEnd - videoStart, 2.9, 3.1);
    }

    [Theory]
    [InlineData("aac", false)]
    [InlineData("ac3", false)]
    [InlineData("aac", true)] // VideoToolbox: no frame reordering, a different delay from libx264's
    public void ProductionChaining_DoesNotOverlapItems(string audioEncoder, bool videoToolbox)
    {
        if (_ff is null || (videoToolbox && (!OperatingSystem.IsMacOS() || !_ff.HasEncoder("h264_videotoolbox"))))
        {
            return;
        }

        var profile = videoToolbox ? VideoToolboxProfile() : Software;

        // Exactly the per-item path, three items long: each item's end is read the way the producer pump reads it
        // (its progress output plus the TS packets it forwarded), and the next item starts at NextTimeline of that.
        // libx264 delays its last frame two frames past ffmpeg's progress position, so chaining from progress alone
        // started every item ~100ms inside the previous one.
        var parts = new List<string>();
        var timeline = TimeSpan.Zero;
        foreach (var fixture in new[] { "progressive_1080p.mkv", "grey_4x3.mkv", "surround_51.mkv" })
        {
            var part = _media.Output("chain.ts");
            var args = StreamArguments.Build(_media[fixture], TimeSpan.Zero, timeline, Width, 2000, profile, audioEncoder, 128, null, softwareDecode: true, initialBurstSeconds: NoPacing);
            var stderr = _ff.RunProducer(args, part);
            var tracker = new StreamSessionService.SpeedTracker(timeline.TotalSeconds);
            foreach (var line in stderr.Split('\r', '\n'))
            {
                tracker.Observe(line, null);
            }

            var scanner = new TsEndScanner();
            scanner.Feed(File.ReadAllBytes(part));
            timeline = StreamSessionService.NextTimeline(timeline, TimeSpan.FromSeconds(4), StreamSessionService.ProducerEndSeconds(tracker, scanner));
            parts.Add(part);
        }

        for (var i = 1; i < parts.Count; i++)
        {
            var before = parts[i - 1];
            var after = parts[i];
            var aVideo = _ff.Packets(before, "v:0");
            var bVideo = _ff.Packets(after, "v:0");
            var aEnd = aVideo.Max(p => p.Pts + p.Duration);
            var bStart = bVideo.Min(p => p.Pts);
            Assert.True(bStart >= aEnd - 0.001, $"item {i + 1} video starts at {bStart:0.000}s, inside item {i} (ends {aEnd:0.000}s)");
            Assert.True(bStart - aEnd < 1.5 / StreamArguments.OutputFps, $"item {i + 1} video starts {bStart - aEnd:0.000}s after item {i} ends");
            Assert.True(bVideo.Min(p => p.Dts) > aVideo.Max(p => p.Dts), $"item {i + 1}'s first DTS {bVideo.Min(p => p.Dts):0.000} is not after item {i}'s last {aVideo.Max(p => p.Dts):0.000}");

            var aAudioEnd = _ff.Packets(before, "a:0").Max(p => p.Pts + p.Duration);
            var bAudioStart = _ff.Packets(after, "a:0").Min(p => p.Pts);
            Assert.True(bAudioStart >= aAudioEnd - 0.001, $"item {i + 1} audio starts at {bAudioStart:0.000}s, inside item {i}'s (ends {aAudioEnd:0.000}s)");
            Assert.True(bAudioStart - aAudioEnd < 0.05, $"item {i + 1} audio starts {bAudioStart - aAudioEnd:0.000}s after item {i}'s ends");
        }
    }

    [Fact]
    public void Concat_CompatibleItems_StayInOneFormat_AndEveryItemIsHeard()
    {
        if (_ff is null)
        {
            return;
        }

        // Items the concat path can follow: one video codec, one audio codec and channel count, with the picture
        // size, frame rate and sample rate changing between them.
        var (output, stderr) = Concat(new[] { "progressive_1080p.mkv", "stereo_44k.mkv", "grey_4x3.mkv" });
        Assert.DoesNotContain("Error submitting packet to decoder", stderr, StringComparison.Ordinal);

        var video = _ff.Stream(output, "video")!.Value;
        Assert.Equal(Width, video.GetProperty("width").GetInt32());
        Assert.Equal(Height, video.GetProperty("height").GetInt32());
        Assert.Equal(2, _ff.Stream(output, "audio")!.Value.GetProperty("channels").GetInt32());
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));
        foreach (var (from, name) in new[] { (1.0, "progressive_1080p"), (7.0, "stereo_44k"), (11.0, "grey_4x3") })
        {
            Assert.True(_ff.MeanVolume(output, from, 2) > -60, name + " is silent on the concat path");
        }
    }

    [Fact]
    public void Concat_PlaysTheFirstAudioTrack_NotTheWidest()
    {
        if (_ff is null)
        {
            return;
        }

        Assert.Equal(2, _ff.Streams(_media["two_track.mkv"]).Count(s => s.GetProperty("codec_type").GetString() == "audio"));

        // The default stereo track carries the tone; the 5.1 track ffmpeg would pick on its own is silent.
        var (output, _) = Concat(new[] { "two_track.mkv" });
        Assert.True(_ff.MeanVolume(output, 1, 2) > -60, "the concat path played the silent 5.1 track");

        // And when the channel chose the second track for its items, that is the one that plays.
        var list = _media.Output("list.txt");
        File.WriteAllLines(list, new[] { "file '" + _media["two_track.mkv"] + "'" });
        var second = _media.Output("concat-second.ts");
        _ff.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, Width, 2000, Software, "aac", 128, initialBurstSeconds: NoPacing, audioOrdinal: 1), second, outputSeconds: 3.5);
        Assert.True(_ff.MeanVolume(second, 1, 2) < -80, "the concat path did not play the chosen second track");
    }

    [Fact]
    public void Concat_CannotFollowAChannelCountChange()
    {
        if (_ff is null)
        {
            return;
        }

        // Why ConcatCompatible exists: the second item's 5.1 audio goes through the decoder set up for the first
        // item's stereo, which rejects it packet after packet (on a live channel that stretch airs as silence,
        // because aresample pads the gap). If a newer ffmpeg ever decodes it cleanly, the rule can be relaxed.
        var (_, stderr) = Concat(new[] { "progressive_1080p.mkv", "surround_51.mkv" });
        Assert.Contains("Error submitting packet to decoder", stderr, StringComparison.Ordinal);

        // The compatible mix decodes without a single rejected packet.
        var (_, clean) = Concat(new[] { "progressive_1080p.mkv", "stereo_44k.mkv" });
        Assert.DoesNotContain("Error submitting packet to decoder", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Slate_MatchesTheChannelFormat()
    {
        if (_ff is null)
        {
            return;
        }

        var output = _media.Output("slate.ts");
        _ff.RunProducer(StreamArguments.BuildSlate(Width, 2000, 2, TimeSpan.Zero, null, Software, "aac", 128), output);

        var video = _ff.Stream(output, "video")!.Value;
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal(Width, video.GetProperty("width").GetInt32());
        Assert.Equal(Height, video.GetProperty("height").GetInt32());
        Assert.Equal(2, _ff.Stream(output, "audio")!.Value.GetProperty("channels").GetInt32());
        Assert.Equal(string.Empty, _ff.DecodeErrors(output));
    }

    [Fact]
    public void Segmenter_CutsEvenSegments()
    {
        if (_ff is null)
        {
            return;
        }

        var producer = Produce("progressive_1080p.mkv");
        var dir = _media.Output("hls");
        Directory.CreateDirectory(dir);
        var args = StreamArguments.BuildHlsSegmenter(Path.Combine(dir, "seg%d.ts"), Path.Combine(dir, "stream.m3u8"), 100);
        var (code, _, err) = FfmpegHarness.Run(_ff.Ffmpeg, args, stdinFile: producer);
        Assert.True(code == 0, err);

        var durations = File.ReadAllLines(Path.Combine(dir, "stream.m3u8"))
            .Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(l => double.Parse(l[8..].TrimEnd(','), CultureInfo.InvariantCulture))
            .ToList();
        Assert.True(durations.Count >= 2, "expected at least two segments");
        foreach (var d in durations.Take(durations.Count - 1))
        {
            Assert.InRange(d, StreamArguments.SegmentSeconds - 0.05, StreamArguments.SegmentSeconds + 0.05);
        }
    }

    [Fact]
    public void VideoToolbox_ProducesTheChannelFormat()
    {
        if (_ff is null || !OperatingSystem.IsMacOS() || !_ff.HasEncoder("h264_videotoolbox"))
        {
            return;
        }

        var profile = VideoToolboxProfile();
        Assert.Equal("h264_videotoolbox", profile.Name);

        foreach (var name in new[] { "progressive_1080p.mkv", "hdr10_pq.mkv" })
        {
            var output = Produce(name, video: profile, isHdr: name.StartsWith("hdr", StringComparison.Ordinal));
            var video = _ff.Stream(output, "video")!.Value;
            Assert.Equal(Width, video.GetProperty("width").GetInt32());
            Assert.Equal(Height, video.GetProperty("height").GetInt32());
            Assert.Equal(string.Empty, _ff.DecodeErrors(output));
        }
    }

    // Runs the given fixtures, in order, through the real continuous concat producer arguments (one pass), and
    // returns the output with the producer's stderr.
    private (string Output, string Stderr) Concat(string[] fixtures)
    {
        var list = _media.Output("list.txt");
        File.WriteAllLines(list, fixtures.Select(n => "file '" + _media[n].Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
        var output = _media.Output("concat.ts");
        var seconds = fixtures.Sum(n => _ff!.Packets(_media[n], "v:0").Max(p => p.Pts + p.Duration)) - 0.5;
        var stderr = _ff!.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, Width, 2000, Software, "aac", 128, initialBurstSeconds: NoPacing), output, outputSeconds: seconds);
        return (output, stderr);
    }

    // The encoder profile the plugin resolves for a server configured for VideoToolbox.
    private static VideoEncoderProfile VideoToolboxProfile()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true };
        var prop = typeof(EncodingOptions).GetProperty("HardwareAccelerationType")!;
        prop.SetValue(options, prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, "videotoolbox", true) : "videotoolbox");
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        return new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>()).ResolveVideo(VideoCodec.H264, allowHardware: true);
    }

    // Runs one item through the real per-item producer arguments and returns the output file.
    private string Produce(
        string fixture,
        TimeSpan offset = default,
        TimeSpan timeline = default,
        bool isHdr = false,
        (int, bool)? forcedSubtitle = null,
        VideoEncoderProfile? video = null,
        TimeSpan? silentAudio = null,
        string? toneMapStage = null)
    {
        var args = StreamArguments.Build(
            _media[fixture],
            offset,
            timeline,
            Width,
            2000,
            video ?? Software,
            "aac",
            128,
            forcedSubtitle,
            softwareDecode: video is null,
            isHdr: isHdr,
            initialBurstSeconds: NoPacing,
            silentAudio: silentAudio,
            toneMapStage: toneMapStage);
        var output = _media.Output(Path.GetFileNameWithoutExtension(fixture) + ".ts");
        _ff!.RunProducer(args, output);
        return output;
    }

    // Asserts the audio and video tracks end together (within two audio frames) and returns both end times.
    private (double VideoEnd, double AudioEnd) AssertAvEndsAligned(string output)
    {
        var videoEnd = _ff!.Packets(output, "v:0").Max(p => p.Pts + p.Duration);
        var audioEnd = _ff.Packets(output, "a:0").Max(p => p.Pts + p.Duration);
        Assert.True(Math.Abs(videoEnd - audioEnd) < 0.05, $"video ends at {videoEnd:0.000}s, audio at {audioEnd:0.000}s");
        return (videoEnd, audioEnd);
    }

    private static void AssertSquarePixels(System.Text.Json.JsonElement video)
    {
        if (video.TryGetProperty("sample_aspect_ratio", out var sar))
        {
            Assert.Contains(sar.GetString(), new[] { "1:1", "0:1" });
        }
    }
}
