using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Standby is a pause, not a dead end: a channel whose content is unreachable shows standby for a bounded hold
/// and then tries its schedule again, so content that comes back is back on screen without the viewer retuning.
/// </summary>
public class StandbyRecoveryTests : IClassFixture<MediaFixtures>
{
    private const int ChannelNumber = 9301;
    private readonly MediaFixtures _media;
    private readonly string _logs;

    public StandbyRecoveryTests(MediaFixtures media)
    {
        _media = media;
        _logs = media.Output("logs");
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 4.0)]
    [InlineData(4, 5.0)] // the ceiling
    [InlineData(40, 5.0)]
    public void StandbyHold_DoublesToAFiveMinuteCeiling(int round, double minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), StreamSessionService.StandbyHold(round - 1, TimeSpan.FromMinutes(1)));

    [Theory(Timeout = 120000)]
    [InlineData(false)] // the continuous concat path
    [InlineData(true)] // the per-item path (a >1080p item forces it)
    public async Task UnreachableContent_ShowsStandby_ThenRejoinsTheScheduleWhenItReturns(bool perItem)
    {
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        await Task.Run(async () =>
        {
            // The channel's only item lives on "storage" that is unreachable at tune-in.
            var mediaPath = _media.Output("returns-later.mkv");
            var program = new ProgramEntry(Guid.NewGuid(), "Returns later", null, TimeSpan.FromSeconds(6).Ticks, mediaPath) { SourceHeight = perItem ? 2160 : 1080, SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2 };
            var number = ChannelNumber + (perItem ? 1 : 0);
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Standby test", Number = number };
            var schedules = (ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>>)typeof(ChannelService)
                .GetField("MemorySchedules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            schedules[number] = new[] { program };
            var contentMarker = perItem ? "Returns later\n$ ffmpeg" : "-f concat";

            var log = _media.Output("session.log");
            var stats = new SessionStats { LogPath = log };
            var output = _media.Output("channel.ts");
            try
            {
                var service = NewService(ff.Ffmpeg);
                using var cts = new CancellationTokenSource();
                var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
                await using (stream.ConfigureAwait(false))
                {
                    var run = service.StreamToAsync(channel, stream, cts.Token, stats);

                    // Standby starts...
                    await WaitForLog(log, "standby for 2s, then retrying the schedule").ConfigureAwait(false);

                    // ...the storage comes back during the hold...
                    File.Copy(_media["progressive_1080p.mkv"], mediaPath);

                    // ...and once the hold ends the channel is back on its schedule, unprompted.
                    await WaitForLog(log, contentMarker, after: "standby for 2s").ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    await cts.CancelAsync().ConfigureAwait(false);
                    await run.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }

                // One unbroken channel stream: standby then content, timestamps only ever moving forward.
                var video = ff.Packets(output, "v:0");
                Assert.True(video.Count > 0);
                for (var i = 1; i < video.Count; i++)
                {
                    Assert.True(video[i].Dts > video[i - 1].Dts, $"video DTS went backwards at packet {i}: {video[i - 1].Dts:0.000} -> {video[i].Dts:0.000}");
                }

                var text = File.ReadAllText(log);
                Assert.True(text.IndexOf("standby for 2s", StringComparison.Ordinal) < text.LastIndexOf(contentMarker, StringComparison.Ordinal));

                // The card said why: the concat path found no file to open, the per-item path saw the item fail.
                var reason = perItem ? StreamSessionService.StandbyReason.ItemsFailing : StreamSessionService.StandbyReason.MediaUnreachable;
                if (Jellyfin.Plugin.LiveChannels.Utilities.FontLocator.Find() is not null)
                {
                    Assert.Contains("text='" + StreamSessionService.StandbyMessage(reason) + "'", text, StringComparison.Ordinal);
                }
            }
            finally
            {
                schedules.TryRemove(number, out _);
            }
        });
    }

    [Fact(Timeout = 120000)]
    public async Task AnUndecodableItem_LeavesAFailureReport()
    {
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        await Task.Run(async () =>
        {
            // A file that exists but is not media: ffmpeg starts, fails, and the channel falls to standby.
            var mediaPath = _media.Output("garbage.mkv");
            File.WriteAllBytes(mediaPath, Enumerable.Range(0, 65536).Select(i => (byte)(i * 31)).ToArray());
            var program = new ProgramEntry(Guid.NewGuid(), "Garbage item", null, TimeSpan.FromSeconds(30).Ticks, mediaPath) { SourceHeight = 2160 };
            const int Number = ChannelNumber + 2;
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Report test", Number = Number };
            var schedules = (ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>>)typeof(ChannelService)
                .GetField("MemorySchedules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            schedules[Number] = new[] { program };
            var log = _media.Output("report-session.log");
            try
            {
                var service = NewService(ff.Ffmpeg);
                using var cts = new CancellationTokenSource();
                using var sink = new MemoryStream();
                var run = service.StreamToAsync(channel, sink, cts.Token, new SessionStats { LogPath = log });
                await WaitForLog(log, "failure report written to").ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
                await run.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            finally
            {
                schedules.TryRemove(Number, out _);
            }

            var report = Directory.GetFiles(_logs, "livechannels-failure-*Garbage_item.log").Single();
            var text = File.ReadAllText(report);
            Assert.Contains("Playing: Garbage item", text, StringComparison.Ordinal);
            Assert.Contains("Outcome: produced no output", text, StringComparison.Ordinal);
            Assert.Contains(mediaPath, text, StringComparison.Ordinal);
            Assert.DoesNotContain("ffmpeg error output:\n(none)", text, StringComparison.Ordinal); // ffmpeg's real error is in it
        });
    }

    private StreamSessionService NewService(string ffmpeg)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg);

        var options = new EncodingOptions { EnableHardwareEncoding = false };
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        var encoders = new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>());

        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_media.Output("cache"));
        paths.DataPath.Returns(_media.Output("data"));

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

        paths.LogDirectoryPath.Returns(_logs);
        return new StreamSessionService(encoder, channels, encoders, intros, paths, Substitute.For<ILogger<StreamSessionService>>())
        {
            StandbyHoldFirst = TimeSpan.FromSeconds(2)
        };
    }

    // Waits until the session log shows the text (after the first occurrence of another, when given).
    private static async Task WaitForLog(string log, string text, string? after = null)
    {
        for (var i = 0; i < 600; i++)
        {
            var content = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
            var from = after is null ? 0 : content.IndexOf(after, StringComparison.Ordinal);
            if (from >= 0 && content.IndexOf(text, from, StringComparison.Ordinal) >= 0)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("never logged: " + text + "\n" + (File.Exists(log) ? File.ReadAllText(log) : "(no log)"));
    }
}
