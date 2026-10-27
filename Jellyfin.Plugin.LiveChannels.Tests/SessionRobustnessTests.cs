using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The stream session's failure handling: a producer cut short mid-item is a failure that resumes the item once in
/// software rather than a "play" that walks the playlist; the output settings are fixed for a session's life; a
/// hung mount reaches standby; a closed output ends the producer at once; older lingers stand down; and series
/// recordings honour "keep up to" and "new episodes only".
/// </summary>
public class SessionRobustnessTests : IClassFixture<MediaFixtures>
{
    private const int ChannelNumber = 9401;
    private readonly MediaFixtures _media;

    public SessionRobustnessTests(MediaFixtures media) => _media = media;

    // ---- An item cut short ----

    [Fact]
    public void ACleanExit_IsNeverCutShort()
        => Assert.Null(StreamSessionService.CutShort(0, false, TimeSpan.Zero, 3, TimeSpan.FromMinutes(30)));

    [Fact]
    public void ACancelledRun_IsNeverCutShort()
        => Assert.Null(StreamSessionService.CutShort(137, true, TimeSpan.Zero, 3, TimeSpan.FromMinutes(30)));

    [Fact]
    public void AFailureWithNoEndReading_IsNotTreatedAsCutShort()
        => Assert.Null(StreamSessionService.CutShort(1, false, TimeSpan.Zero, -1, TimeSpan.FromMinutes(30)));

    [Fact]
    public void AFailureFarFromTheEnd_IsCutShort_AtWhatWasWritten()
    {
        // The item started at 100s on the channel timeline and the output ended at 112.5s: 12.5s of a 30-minute item.
        var cut = StreamSessionService.CutShort(137, false, TimeSpan.FromSeconds(100), 112.5, TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromSeconds(12.5), cut);
    }

    [Fact]
    public void AFailureAtTheVeryEnd_LosesNothingWorthReplaying()
        => Assert.Null(StreamSessionService.CutShort(1, false, TimeSpan.Zero, 1797, TimeSpan.FromMinutes(30)));

    [Fact]
    public void TheResume_PicksUpWhereTheOutputStopped()
    {
        var program = new ProgramEntry(Guid.NewGuid(), "Item", null, TimeSpan.FromMinutes(30).Ticks, "/a.mkv");
        var resumed = StreamSessionService.ResumeAfter(new StreamStep(program, TimeSpan.FromMinutes(5), null), TimeSpan.FromSeconds(40));
        Assert.Same(program, resumed.Program);
        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(40), resumed.Offset);
        Assert.Null(resumed.DurationLimit);

        // A step the schedule cuts off (a daypart boundary) still ends where it would have.
        var limited = StreamSessionService.ResumeAfter(new StreamStep(program, TimeSpan.Zero, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(4));
        Assert.Equal(TimeSpan.FromMinutes(6), limited.DurationLimit);
    }

    // ---- Output settings are fixed for the session ----

    [Fact]
    public void OutputSettings_ArePinnedAtTheFirstRead()
    {
        var stats = new SessionStats();
        var h264 = new OutputSettings(1280, 4000, VideoCodec.H264, AudioCodec.Aac);
        var hevc = new OutputSettings(1920, 8000, VideoCodec.Hevc, AudioCodec.Ac3);

        Assert.Same(h264, stats.PinOutput(() => h264));

        // A save switched to HEVC/AC-3 mid-session: the session keeps encoding what its viewers set up for.
        var reads = 0;
        Assert.Same(h264, stats.PinOutput(() => { reads++; return hevc; }));
        Assert.Equal(0, reads);
    }

    // ---- Hung storage ----

    [Fact(Timeout = 30000)]
    public async Task AHungMount_IsGivenUpOn_AndEveryLaterCheckAnswersAtOnce()
    {
        await Task.Run(() =>
        {
            using var release = new ManualResetEventSlim();
            var probe = new StorageProbe(TimeSpan.FromMilliseconds(300), _ =>
            {
                release.Wait();
                return true;
            });

            var watch = Stopwatch.StartNew();
            Assert.False(probe.Exists("/mnt/nfs/a.mkv"));
            Assert.True(probe.Hung);
            Assert.False(probe.Exists("/mnt/nfs/b.mkv"));
            Assert.False(probe.Exists("/mnt/nfs/c.mkv"));
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            release.Set();
        });
    }

    [Fact]
    public void HealthyStorage_AnswersNormally()
    {
        var probe = new StorageProbe();
        Assert.True(probe.Exists(_media["progressive_1080p.mkv"]));
        Assert.False(probe.Exists(_media.Output("does-not-exist.mkv")));
        Assert.False(probe.Hung);
    }

    // ---- A line break in a file name keeps the item off the concat list ----

    [Theory]
    [InlineData("/media/Show\nfile '/etc/hosts'.mkv")]
    [InlineData("/media/Show\r.mkv")]
    public void AFileNameWithALineBreak_IsNotConcatCompatible(string path)
    {
        ProgramEntry Item(string p) => new(Guid.NewGuid(), "x", null, TimeSpan.FromMinutes(1).Ticks, p) { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2 };
        Assert.True(StreamSessionService.ConcatCompatible(new[] { Item("/media/a.mkv"), Item("/media/b.mkv") }));
        Assert.False(StreamSessionService.ConcatCompatible(new[] { Item("/media/a.mkv"), Item(path) }));
    }

    // ---- Lingers ----

    [Fact]
    public void AnOlderLinger_StandsDown_ForTheOneAViewerReturningStarted()
    {
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = first.AddSeconds(25);
        Assert.True(LiveChannelsTvService.LingerIsCurrent(first, first));
        Assert.False(LiveChannelsTvService.LingerIsCurrent(second, first)); // left, came back, left again
        Assert.False(LiveChannelsTvService.LingerIsCurrent(null, first)); // someone is watching again
        Assert.True(LiveChannelsTvService.LingerIsCurrent(second, null));
    }

    // ---- Series recordings ----

    [Fact]
    public void KeepUpTo_ReturnsOnlyTheRulesOwnOldestRecordings()
    {
        var dir = _media.Output("keep-" + Guid.NewGuid().ToString("N"));
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(dir);
        var timers = new TimerService(paths, NullLogger<TimerService>.Instance);
        var rule = timers.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ProgramId = "chan_1", KeepUpTo = 2 });
        var other = timers.SaveSeriesTimer(new SeriesTimerInfo { Name = "Other", ProgramId = "chan_2" });

        void Record(string ruleId, string file)
        {
            var id = timers.SaveTimer(new TimerInfo { ProgramId = "p_" + file, SeriesTimerId = ruleId, StartDate = DateTime.UtcNow.AddHours(-2), EndDate = DateTime.UtcNow.AddHours(-1) });
            Assert.True(timers.CompleteTimer(id, RecordingStatus.Completed, "/rec/" + file));
            Thread.Sleep(5); // distinct timestamps
        }

        Record(rule, "e1.mkv");
        Record(other, "o1.mkv");
        Record(rule, "e2.mkv");
        Record(rule, "e3.mkv");
        Record(rule, "e3.mkv"); // a repeat airing adopting the same file counts once

        Assert.Equal(new[] { "/rec/e1.mkv" }, timers.TakeExcessRecordings(rule, 2));
        Assert.Empty(timers.TakeExcessRecordings(rule, 2));
        Assert.Empty(timers.TakeExcessRecordings(other, 0)); // zero keeps everything

        // The list survives a restart.
        var reloaded = new TimerService(paths, NullLogger<TimerService>.Instance);
        Assert.Equal(new[] { "/rec/e2.mkv" }, reloaded.TakeExcessRecordings(rule, 1));
    }

    [Fact]
    public void KeepUpTo_NeverCountsAFileTheRuleAdopted()
    {
        var dir = _media.Output("adopt-" + Guid.NewGuid().ToString("N"));
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(dir);
        var timers = new TimerService(paths, NullLogger<TimerService>.Instance);
        var rule = timers.SaveSeriesTimer(new SeriesTimerInfo { Name = "Show", ProgramId = "chan_1", KeepUpTo = 1 });

        // A one-off recording of an episode already sat at the target, so the series child adopted it.
        var adopted = timers.SaveTimer(new TimerInfo { ProgramId = "p_a", SeriesTimerId = rule, StartDate = DateTime.UtcNow.AddHours(-3), EndDate = DateTime.UtcNow.AddHours(-2) });
        Assert.True(timers.CompleteTimer(adopted, RecordingStatus.Completed, "/rec/one-off.mkv", adopted: true));
        Thread.Sleep(5);
        var made = timers.SaveTimer(new TimerInfo { ProgramId = "p_b", SeriesTimerId = rule, StartDate = DateTime.UtcNow.AddHours(-2), EndDate = DateTime.UtcNow.AddHours(-1) });
        Assert.True(timers.CompleteTimer(made, RecordingStatus.Completed, "/rec/made.mkv"));

        Assert.Empty(timers.TakeExcessRecordings(rule, 1));
    }

    [Fact]
    public void NewOnly_FollowsTheGuidesNewWindow()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var fresh = new ProgramEntry(Guid.NewGuid(), "x", null, 1, "/x") { DateAdded = now.AddDays(-3) };
        var old = new ProgramEntry(Guid.NewGuid(), "y", null, 1, "/y") { DateAdded = now.AddDays(-30) };
        Assert.True(fresh.IsNewAt(now));
        Assert.False(old.IsNewAt(now));
    }

    // ---- A closed output ends the producer at once ----

    [Fact(Timeout = 120000)]
    public async Task AClosedOutput_EndsTheSession_WithoutAFailureCascade()
    {
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        await Task.Run(async () =>
        {
            // Per-item (a >1080p item forces it), three items, and an output that dies after the first few KB: the
            // segmenter is gone. Before, each item was spawned (and retried in software), "failed", and wrote a
            // misleading report, until standby; now the producer ends at the first failed write.
            var mediaPath = _media["progressive_1080p.mkv"];
            var programs = Enumerable.Range(0, 3)
                .Select(i => new ProgramEntry(Guid.NewGuid(), "Item " + i, null, TimeSpan.FromSeconds(6).Ticks, mediaPath) { SourceHeight = 2160 })
                .ToArray();
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Closed output test", Number = ChannelNumber };
            var schedules = (ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>>)typeof(ChannelService)
                .GetField("MemorySchedules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            schedules[ChannelNumber] = programs;
            var logs = _media.Output("closed-output-logs-" + Guid.NewGuid().ToString("N"));
            try
            {
                var service = NewService(ff.Ffmpeg, logs);
                using var output = new FailingStream(failAfterBytes: 4096);
                var watch = Stopwatch.StartNew();
                await Assert.ThrowsAsync<OutputClosedException>(() => service.StreamToAsync(channel, output, CancellationToken.None, new SessionStats())).ConfigureAwait(false);
                Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(60));
                Assert.Equal(1, output.FailedWrites);
                Assert.False(Directory.Exists(logs) && Directory.EnumerateFiles(logs, "livechannels-failure-*").Any(), "a closed output wrote failure reports");
            }
            finally
            {
                schedules.TryRemove(ChannelNumber, out _);
            }
        });
    }

    private StreamSessionService NewService(string ffmpeg, string logs)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg);

        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(new EncodingOptions { EnableHardwareEncoding = false });
        var encoders = new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>());

        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_media.Output("cache"));
        paths.DataPath.Returns(_media.Output("data"));
        paths.LogDirectoryPath.Returns(logs);

        var channels = new ChannelService(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            Substitute.For<ILogger<ChannelService>>());
        var intros = new IntroService(encoder, encoders, paths, Substitute.For<ILogger<IntroService>>());
        return new StreamSessionService(encoder, channels, encoders, intros, paths, Substitute.For<ILogger<StreamSessionService>>());
    }

    // A write-only stream that accepts some bytes and then fails every write, as a pipe to a dead process does.
    private sealed class FailingStream : Stream
    {
        private readonly long _failAfter;
        private long _written;

        public FailingStream(long failAfterBytes) => _failAfter = failAfterBytes;

        public int FailedWrites { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_written >= _failAfter)
            {
                FailedWrites++;
                throw new IOException("Broken pipe");
            }

            _written += count;
        }
    }
}
