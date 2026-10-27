using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The TS scanner reads where a producer's picture and sound really end from the bytes the pump already forwards,
/// so the next producer can start exactly there whatever the encoder's frame-reorder delay.
/// </summary>
public class TsEndScannerTests : IClassFixture<MediaFixtures>
{
    private static readonly VideoEncoderProfile Software =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private readonly MediaFixtures _media;
    private readonly ITestOutputHelper _output;

    public TsEndScannerTests(MediaFixtures media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("ac3")]
    public void MatchesFfprobe_OnARealProducer_FedInRaggedChunks(string audioEncoder)
    {
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        var output = _media.Output("scan.ts");
        var args = StreamArguments.Build(_media["progressive_1080p.mkv"], TimeSpan.Zero, TimeSpan.FromSeconds(100), 1280, 2000, Software, audioEncoder, 128, null, softwareDecode: true, initialBurstSeconds: 600);
        ff.RunProducer(args, output);

        var scanner = new TsEndScanner();
        var bytes = File.ReadAllBytes(output);
        var random = new Random(7);
        for (var at = 0; at < bytes.Length;)
        {
            var take = Math.Min(bytes.Length - at, random.Next(1, 5000));
            scanner.Feed(bytes.AsSpan(at, take));
            at += take;
        }

        var video = ff.Packets(output, "v:0");
        var audio = ff.Packets(output, "a:0");
        _output.WriteLine($"video max {video.Max(p => p.Pts):0.000000} scanned {scanner.MaxVideoPts:0.000000}");
        _output.WriteLine($"audio last {audio.Max(p => p.Pts):0.000000} (+{audio[^1].Duration:0.000000}) scanned {scanner.LastAudioPts:0.000000} frame {scanner.AudioFrameSeconds:0.000000}");

        Assert.Equal(video.Max(p => p.Pts), scanner.MaxVideoPts!.Value, 3);
        Assert.Equal(audio.Max(p => p.Pts), scanner.LastAudioPts!.Value, 3);
        Assert.Equal(audio[^1].Duration, scanner.AudioFrameSeconds!.Value, 3);
    }

    [Fact]
    public void NextStart_ClearsTheLastVideoFrame_AndTheLastAudioFramePlusPriming()
    {
        // An AAC-style output: sound primed one frame (21.3 ms) ahead of the picture at 100.0.
        var scanner = new TsEndScanner();
        scanner.Feed(Pes(0xE0, 100.0));
        scanner.Feed(Pes(0xC0, 99.9787));
        scanner.Feed(Pes(0xE0, 100.1)); // decode order: a later frame first
        scanner.Feed(Pes(0xE0, 100.0333));
        scanner.Feed(Pes(0xC0, 100.0));
        Assert.Equal(0.0213, scanner.AudioLeadSeconds, 3);

        // Video ends at 100.1 + 1/30, after the audio (100.0 + 0.0213) plus the next item's priming.
        Assert.Equal(100.1 + (1.0 / 30), scanner.NextStartSeconds(1.0 / 30, 100)!.Value, 3);

        // When the sound runs past the picture, the next item waits for it and for its own priming.
        scanner.Feed(Pes(0xC0, 100.2)); // AC-3 rides private stream 1 too; here the frame spacing grows to 0.2
        Assert.Equal(100.2 + 0.2 + 0.0213, scanner.NextStartSeconds(1.0 / 30, 100)!.Value, 3);
    }

    [Fact]
    public void TimestampsKeepCountingAcrossThe33BitWrap()
    {
        // 26.5 hours into a session the 90 kHz PTS wraps to zero; the channel timeline does not.
        const double wrap = 8589934592.0 / 90000.0;
        var scanner = new TsEndScanner();
        scanner.Feed(Pes(0xBD, wrap - 0.05));
        scanner.Feed(Pes(0xE0, wrap - 0.0333));
        scanner.Feed(Pes(0xE0, 0.0)); // wrapped

        Assert.Equal(wrap + (1.0 / 30), scanner.NextStartSeconds(1.0 / 30, wrap)!.Value, 3);

        // A producer that starts after the wrap is placed on the session's lap, not back at zero.
        var later = new TsEndScanner();
        later.Feed(Pes(0xE0, 10.0));
        Assert.Equal(wrap + 10.0 + (1.0 / 30), later.NextStartSeconds(1.0 / 30, wrap + 9.5)!.Value, 3);
    }

    [Fact]
    public void AudioArrivingFirst_AcrossTheWrap_StaysOnTheVideosLap()
    {
        // The sound starts 21 ms before the picture and the 33-bit clock wraps between them: the picture's first
        // timestamp must land on the sound's lap, not a lap behind it.
        const double wrap = 8589934592.0 / 90000.0;
        var scanner = new TsEndScanner();
        scanner.Feed(Pes(0xC0, wrap - 0.0213));
        scanner.Feed(Pes(0xE0, 0.0));
        scanner.Feed(Pes(0xC0, 0.0));
        Assert.Equal(wrap, scanner.MinVideoPts!.Value, 3);
        Assert.Equal(0.0213, scanner.AudioLeadSeconds, 3);
    }

    [Fact]
    public void TheSessionTimeline_IsTheNextStart_NotAFrameLater()
    {
        // The session reports one frame past what it records, so it records the last frame's start: a restarted
        // producer then starts exactly where the next item would have.
        var scanner = new TsEndScanner();
        scanner.Feed(Pes(0xE0, 100.0));
        scanner.Feed(Pes(0xE0, 101.0));
        var stats = new Jellyfin.Plugin.LiveChannels.Services.SessionStats();
        var tracker = new Jellyfin.Plugin.LiveChannels.Services.StreamSessionService.SpeedTracker(100);
        var end = Jellyfin.Plugin.LiveChannels.Services.StreamSessionService.ProducerEndSeconds(tracker, scanner, stats);
        Assert.Equal(101.0, end, 3);
        Assert.Equal(101.0 + (1.0 / 30), stats.TimelineSeconds, 3);
    }

    [Fact]
    public void NoVideo_GivesNoReading()
    {
        var scanner = new TsEndScanner();
        scanner.Feed(Pes(0xC0, 5.0));
        Assert.Null(scanner.NextStartSeconds(1.0 / 30, 5));
    }

    // One 188-byte TS packet that starts a PES packet with the given stream id and PTS.
    private static byte[] Pes(byte streamId, double seconds)
    {
        var pts = (long)Math.Round(seconds * 90000) & 0x1FFFFFFFFL;
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = 0x41; // payload_unit_start_indicator, PID 0x100
        packet[2] = 0x00;
        packet[3] = 0x10; // payload only
        var pes = new byte[]
        {
            0x00, 0x00, 0x01, streamId, 0x00, 0x00, 0x80, 0x80, 0x05,
            (byte)(0x21 | ((pts >> 29) & 0x0E)),
            (byte)(pts >> 22),
            (byte)(0x01 | ((pts >> 14) & 0xFE)),
            (byte)(pts >> 7),
            (byte)(0x01 | ((pts << 1) & 0xFE))
        };
        pes.CopyTo(packet, 4);
        return packet;
    }
}
