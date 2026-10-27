using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
/// Streams channels with Up Next bumpers end to end through jellyfin-ffmpeg: a tune-in during a bumper shows black
/// until the item starts, on time and from its start, and a slot reached from the previous item plays its rendered
/// bumper and then the item. Skipped quietly without jellyfin-ffmpeg.
/// </summary>
public sealed class BumperStreamTests : IClassFixture<MediaFixtures>
{
    // Clear of the numbers the other streaming tests put in the shared in-memory schedule.
    private const int ChannelNumber = 9501;
    private readonly MediaFixtures _media;

    public BumperStreamTests(MediaFixtures media)
    {
        _media = media;
    }

    [Fact(Timeout = 180000)]
    public async Task ATuneInDuringABumper_ShowsBlack_UntilTheItemStartsOnTime()
    {
        if (FfmpegHarness.Instance is not { } ff)
        {
            return;
        }

        await Task.Run(async () =>
        {
            var item = Item("Bumper join item");
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Bumper join", Number = ChannelNumber };

            // The item's slot began five seconds ago, so ten seconds of its bumper are left.
            var loop = new ProgramLoop(new[] { item }, DateTime.UtcNow.AddSeconds(-5), BumperService.BumperLength.Ticks);
            var log = _media.Output("join.log");
            var output = _media.Output("join.ts");
            await Stream(ff, channel, loop, log, output, "Bumper join item\n$ ffmpeg", after: "black before", untilSeconds: 13).ConfigureAwait(false);

            var text = File.ReadAllText(log);
            Assert.Contains("Tune-in during an Up Next bumper", text, StringComparison.Ordinal);
            Assert.Contains("Joined partway into Up Next bumper", text, StringComparison.Ordinal);
            Assert.True(text.IndexOf("black before", StringComparison.Ordinal) < text.IndexOf("Bumper join item\n$ ffmpeg", StringComparison.Ordinal));

            // Black for the rest of the bumper, then the item from its first frame, about ten seconds in.
            Assert.True(ff.MeanLuma(ff.Frame(output, _media.Output("join-black.png"), 4)) < 20);
            Assert.True(ff.MeanLuma(ff.Frame(output, _media.Output("join-item.png"), 11.5)) > 40);
            AssertMonotonic(ff, output);
        });
    }

    [Fact(Timeout = 240000)]
    public async Task ASlotReachedFromThePreviousItem_PlaysItsBumper_ThenTheItem()
    {
        if (FfmpegHarness.Instance is not { } ff)
        {
            return;
        }

        await Task.Run(async () =>
        {
            var first = Item("First item");
            var second = Item("Second item");
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Bumper slot", Number = ChannelNumber + 1 };

            // The second item's bumper is already rendered, as it would be by the time the first item ends; its
            // Then line names the first item again, whose next slot starts after both slots. The stream joins one
            // second into the first item itself (its slot began sixteen seconds earlier, bumper included), with the
            // clock taken once the render is done; the Then time is only to the minute, so a render that straddles
            // a minute boundary is redone.
            var service = NewService(ff, out var bumpers);
            var width = StreamSessionService.OutputFor(null).Width;
            var slot = BumperService.BumperLength + TimeSpan.FromTicks(first.DurationTicks);
            ProgramLoop loop = null!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var estimate = DateTime.UtcNow.AddSeconds(-16);
                var expected = StreamSessionService.ThenFor((first, estimate + slot + slot));
                bumpers.Prepare(channel, second, width, null, expected);
                for (var i = 0; i < 1200 && bumpers.TryGetCached(channel, second, width, null, expected) is null; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                var anchor = DateTime.UtcNow.AddSeconds(-16);
                loop = new ProgramLoop(new[] { first, second }, anchor, BumperService.BumperLength.Ticks);
                if (bumpers.TryGetCached(channel, second, width, null, StreamSessionService.ThenFor((first, anchor + slot + slot))) is not null)
                {
                    break;
                }
            }
            var log = _media.Output("slot.log");
            var output = _media.Output("slot.ts");
            await Stream(ff, channel, loop, log, output, "Second item\n$ ffmpeg", after: "Up Next: Second item", untilSeconds: 14, service).ConfigureAwait(false);

            var text = File.ReadAllText(log);
            Assert.DoesNotContain("Tune-in during an Up Next bumper", text, StringComparison.Ordinal);
            var firstAt = text.IndexOf("First item\n$ ffmpeg", StringComparison.Ordinal);
            var bumperAt = text.IndexOf("Up Next: Second item", StringComparison.Ordinal);
            var secondAt = text.IndexOf("Second item\n$ ffmpeg", StringComparison.Ordinal);
            Assert.True(firstAt >= 0 && firstAt < bumperAt && bumperAt < secondAt);

            // About five seconds of the first item, then the bumper (lit, not black), then the second item.
            Assert.True(ff.MeanLuma(ff.Frame(output, _media.Output("slot-bumper.png"), 12)) > 15);
            AssertMonotonic(ff, output);
        });
    }

    private static void AssertMonotonic(FfmpegHarness ff, string output)
    {
        var video = ff.Packets(output, "v:0");
        Assert.True(video.Count > 0);
        for (var i = 1; i < video.Count; i++)
        {
            Assert.True(video[i].Dts > video[i - 1].Dts, $"video DTS went backwards at packet {i}: {video[i - 1].Dts:0.000} -> {video[i].Dts:0.000}");
        }
    }

    // Streams the channel until the log shows the given producer start and the output covers the given length, so
    // a machine under load still has the stretch the test looks at.
    private async Task Stream(FfmpegHarness ff, Channel channel, ProgramLoop loop, string log, string output, string until, string after, double untilSeconds, StreamSessionService? service = null)
    {
        var schedules = (ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>>)typeof(ChannelService)
            .GetField("MemorySchedules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        schedules[channel.Number] = loop;
        try
        {
            service ??= NewService(ff, out _);
            using var cts = new CancellationTokenSource();
            var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using (stream.ConfigureAwait(false))
            {
                var run = service.StreamToAsync(channel, stream, cts.Token, new SessionStats { LogPath = log });
                await WaitForLog(log, until, after).ConfigureAwait(false);
                for (var i = 0; i < 120 && Covered(ff, output) < untilSeconds; i++)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }

                Assert.True(Covered(ff, output) >= untilSeconds, "the stream never reached " + untilSeconds + "s");
                await cts.CancelAsync().ConfigureAwait(false);
                await run.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
        }
        finally
        {
            schedules.TryRemove(channel.Number, out _);
        }
    }

    // How far the output's video reaches so far, in seconds.
    private static double Covered(FfmpegHarness ff, string output)
    {
        try
        {
            var packets = ff.Packets(output, "v:0");
            return packets.Count == 0 ? 0 : packets[^1].Pts;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private ProgramEntry Item(string title)
    {
        var path = _media.Output(title.Replace(' ', '-') + ".mkv");
        File.Copy(_media["progressive_1080p.mkv"], path);
        return new ProgramEntry(Guid.NewGuid(), title, null, TimeSpan.FromSeconds(6).Ticks, path)
        {
            ItemKind = "Movie",
            IsMovie = true,
            RawName = title,
            OfficialRating = "PG",
            SourceHeight = 1080,
            SourceVideoCodec = "h264",
            SourceAudioCodec = "aac",
            SourceAudioChannels = 2,
        };
    }

    private StreamSessionService NewService(FfmpegHarness ff, out BumperService bumpers)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ff.Ffmpeg);
        encoder.ProbePath.Returns(ff.Ffprobe);

        var options = new EncodingOptions { EnableHardwareEncoding = false };
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        var encoders = new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>());

        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_media.Output("cache"));
        paths.DataPath.Returns(_media.Output("data"));
        paths.LogDirectoryPath.Returns(_media.Output("logs"));

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
        bumpers = new BumperService(encoder, paths, Substitute.For<ILogger<BumperService>>());
        return new StreamSessionService(encoder, channels, encoders, intros, paths, Substitute.For<ILogger<StreamSessionService>>(), bumpers);
    }

    private static async Task WaitForLog(string log, string text, string? after = null)
    {
        for (var i = 0; i < 1200; i++)
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
