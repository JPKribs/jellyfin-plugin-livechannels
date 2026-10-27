using System;
using System.Collections.Generic;
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
using Xunit.Abstractions;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Picture and sound stay in sync on every path a channel takes: sync clips (a white flash and a beep at the same
/// instants) go through the real producer arguments and the time between each flash and its beep is measured in
/// the output. Uses only arguments the released plugin also has, so the same file measures any version.
/// </summary>
public class AvSyncTests : IClassFixture<MediaFixtures>
{
    // One output frame plus margin. Measured on real timestamps a correct item lands at 0 ms, but a source whose
    // frame rate is converted (23.976 to 30) can only show the flash on a 30 fps frame boundary, up to 33 ms off,
    // and that is quantisation, not drift. A real desync is a steady offset well beyond a frame. The released
    // plugin measures the same on every case here (run this file against it with MASTER_BASELINE defined).
    private const double Tolerance = (1.0 / StreamArguments.OutputFps) + 0.010;

    private static readonly VideoEncoderProfile Software =
        new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private readonly MediaFixtures _media;
    private readonly ITestOutputHelper _log;
    private readonly FfmpegHarness? _ff = FfmpegHarness.Instance;

    public AvSyncTests(MediaFixtures media, ITestOutputHelper log)
    {
        _media = media;
        _log = log;
    }

    [Theory]
    [InlineData("sync_30.mkv", 0.0)]
    [InlineData("sync_30.mkv", 2.5)] // tuning in mid-item seeks
    [InlineData("sync_2398.mkv", 0.0)] // frame rate converted to 30
    [InlineData("sync_2398.mkv", 4.2)]
    [InlineData("sync_25i_ac3.mkv", 0.0)] // deinterlaced, 5.1 AC-3 downmixed
    [InlineData("sync_25i_ac3.mkv", 2.5)]
    [InlineData("sync_audio_delayed.mkv", 0.0)] // the container's own audio offset must survive
    [InlineData("sync_audio_delayed.mkv", 2.5)]
    [InlineData("sync_ts_start10.ts", 0.0)] // timestamps that do not start at zero
    [InlineData("sync_ts_start10.ts", 2.5)]
    public void PerItem_StaysInSync(string fixture, double seek)
    {
        if (_ff is null)
        {
            return;
        }

        var args = StreamArguments.Build(_media[fixture], TimeSpan.FromSeconds(seek), TimeSpan.FromSeconds(100), 1280, 4000, Software, "aac", 128, null, softwareDecode: true, initialBurstSeconds: 600);
        AssertInSync(Run(args), fixture + " seek " + seek, expectedEvents: seek > 0 ? 3 : 5);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.5)] // the burn-in seek runs under -copyts
    public void BurnIn_StaysInSync(double seek)
    {
        if (_ff is null || !_ff.HasFilter("subtitles"))
        {
            return;
        }

        var args = StreamArguments.Build(_media["sync_subs.mkv"], TimeSpan.FromSeconds(seek), TimeSpan.FromSeconds(100), 1280, 4000, Software, "aac", 128, (0, true), softwareDecode: true, initialBurstSeconds: 600);
        AssertInSync(Run(args), "burn-in seek " + seek, expectedEvents: seek > 0 ? 3 : 5);
    }

    [Fact]
    public void Hdr_StaysInSync()
    {
        if (_ff is null)
        {
            return;
        }

        var args = StreamArguments.Build(_media["sync_hdr10.mkv"], TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(100), 1280, 4000, Software, "aac", 128, null, softwareDecode: true, isHdr: true, initialBurstSeconds: 600);
        AssertInSync(Run(args), "HDR seek 2.5", expectedEvents: 3);
    }

    [Fact]
    public void Concat_StaysInSync_AcrossItems()
    {
        if (_ff is null)
        {
            return;
        }

        var list = _media.Output("sync-list.txt");
        File.WriteAllLines(list, new[] { "sync_30.mkv", "sync_2398.mkv", "sync_30.mkv" }.Select(n => "file '" + _media[n] + "'"));
        var output = _media.Output("sync-concat.ts");
        // From the start of the first item: a seek on the concat demuxer starts the picture at the next keyframe
        // (sound from the seek point), a tune-in behaviour of every release that the tune-in card covers; this
        // measures sync across the item changes.
        _ff.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, 1280, 4000, Software, "aac", 128, null, 600), output, outputSeconds: 29.5);
        AssertInSync(output, "concat", expectedEvents: 15);
    }

    [Fact]
    public void Concat_DoesNotDrift_OverManyItems()
    {
        if (_ff is null)
        {
            return;
        }

        // Twelve item changes through one continuous producer. Each change leaves the audio a few milliseconds
        // off; this must not build up. (With the old 100 ms resync threshold it reached 86 ms early.)
        var list = _media.Output("drift-list.txt");
        File.WriteAllLines(list, Enumerable.Range(0, 12).Select(i => "file '" + _media[i % 2 == 0 ? "sync_30.mkv" : "sync_2398.mkv"] + "'"));
        var output = _media.Output("drift.ts");
        _ff.RunProducer(StreamArguments.BuildConcat(list, TimeSpan.Zero, 1280, 4000, Software, "aac", 128, null, 6000), output, outputSeconds: 119);
        AssertInSync(output, "concat x12", expectedEvents: 55);
    }

    [Fact]
    public void ProductionChain_StaysInSync_AcrossSeams()
    {
        if (_ff is null)
        {
            return;
        }

        // Three items played back to back exactly as the per-item path chains them, one starting mid-item, the
        // bytes joined as the segmenter receives them. Every flash in every item must still meet its beep.
        var joined = _media.Output("sync-chain.ts");
        var timeline = TimeSpan.FromSeconds(100);
        using (var outStream = File.Create(joined))
        {
            foreach (var (fixture, seek) in new[] { ("sync_30.mkv", 2.5), ("sync_25i_ac3.mkv", 0.0), ("sync_audio_delayed.mkv", 0.0), ("sync_2398.mkv", 0.0) })
            {
                var part = _media.Output("sync-part.ts");
                var args = StreamArguments.Build(_media[fixture], TimeSpan.FromSeconds(seek), timeline, 1280, 4000, Software, "aac", 128, null, softwareDecode: true, initialBurstSeconds: 600);
                var stderr = _ff.RunProducer(args, part);
                timeline = StreamSessionService.NextTimeline(timeline, TimeSpan.FromSeconds(10 - seek), ProductionEnd(stderr, part, timeline));
                using var inStream = File.OpenRead(part);
                inStream.CopyTo(outStream);
            }
        }

        AssertInSync(joined, "chain of four", expectedEvents: 18);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(4.2)]
    public void BurnInTuneIn_ChainsIntoTheNextItem_WithoutOverlapOrDrift(double seek)
    {
        if (_ff is null || !_ff.HasFilter("subtitles"))
        {
            return;
        }

        // A burn-in channel's first item after a tune-in seeks under -copyts; the next item chains from where its
        // output actually ended. Sound and picture must stay together across that seam, and neither may run
        // backwards into the previous item.
        var joined = _media.Output("burn-chain.ts");
        var timeline = TimeSpan.FromSeconds(100);
        using (var outStream = File.Create(joined))
        {
            foreach (var (fixture, offset, subtitle) in new[] { ("sync_subs.mkv", seek, ((int, bool)?)(0, true)), ("sync_subs.mkv", 0.0, (0, true)), ("sync_30.mkv", 0.0, null) })
            {
                var part = _media.Output("burn-part.ts");
                var args = StreamArguments.Build(_media[fixture], TimeSpan.FromSeconds(offset), timeline, 1280, 4000, Software, "aac", 128, subtitle, softwareDecode: true, initialBurstSeconds: 600);
                var stderr = _ff.RunProducer(args, part);
                timeline = StreamSessionService.NextTimeline(timeline, TimeSpan.FromSeconds(10 - offset), ProductionEnd(stderr, part, timeline));
                using var inStream = File.OpenRead(part);
                inStream.CopyTo(outStream);
            }
        }

        foreach (var selector in new[] { "v:0", "a:0" })
        {
            var packets = _ff.Packets(joined, selector);
            for (var i = 1; i < packets.Count; i++)
            {
                Assert.True(packets[i].Dts > packets[i - 1].Dts, $"{selector} DTS went backwards at packet {i}: {packets[i - 1].Dts:0.000} -> {packets[i].Dts:0.000}");
            }
        }

        AssertInSync(joined, "burn-in tune-in chain", expectedEvents: 12);
    }

    [Theory]
    [InlineData("sync_30.mkv")]
    [InlineData("sync_2398.mkv")]
    public void VideoToolbox_StaysInSync(string fixture)
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
        var args = StreamArguments.Build(_media[fixture], TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(100), 1280, 4000, profile, "aac", 128, null, initialBurstSeconds: 600);
        AssertInSync(Run(args), "VideoToolbox " + fixture, expectedEvents: 3);
    }

    // Where the per-item path chains the next item from: the production end calculation (it reads the forwarded TS
    // packets), or, when this file is compiled against the released plugin to compare (MASTER_BASELINE), the
    // progress position that release chained from.
    private static double ProductionEnd(string stderr, string part, TimeSpan timeline)
    {
        var tracker = new StreamSessionService.SpeedTracker(timeline.TotalSeconds);
        foreach (var line in stderr.Split('\r', '\n'))
        {
            tracker.Observe(line, null);
        }

#if MASTER_BASELINE
        _ = part;
        return tracker.LastContentSeconds;
#else
        var scanner = new TsEndScanner();
        scanner.Feed(File.ReadAllBytes(part));
        return StreamSessionService.ProducerEndSeconds(tracker, scanner);
#endif
    }

    private string Run(List<string> args)
    {
        var output = _media.Output("sync.ts");
        _ff!.RunProducer(args, output);
        return output;
    }

    private void AssertInSync(string output, string label, int expectedEvents)
    {
        var (flashes, beeps, offsets) = _ff!.Sync(output);
        _log.WriteLine($"{label}: flashes at {string.Join(", ", flashes.Select(f => f.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}; beeps at {string.Join(", ", beeps.Select(f => f.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}");
        _log.WriteLine($"{label}: {flashes.Count} flashes, {beeps.Count} beeps, offsets ms: {string.Join(", ", offsets.Select(o => (o * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.True(offsets.Count >= expectedEvents, $"{label}: only {offsets.Count} flash/beep pairs found ({flashes.Count} flashes, {beeps.Count} beeps)");
        var worst = offsets.OrderByDescending(Math.Abs).First();
        Assert.True(Math.Abs(worst) <= Tolerance, $"{label}: picture and sound {worst * 1000:0} ms apart (offsets ms: {string.Join(", ", offsets.Select(o => (o * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture)))})");
    }
}
