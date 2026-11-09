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
/// Streams a channel through a placeholder window end to end through jellyfin-ffmpeg: a tune-in during the window
/// plays the block's card back to back, lit and with a continuous timeline. Skipped quietly without jellyfin-ffmpeg.
/// </summary>
public sealed class PlaceholderStreamTests : IClassFixture<MediaFixtures>
{
    // Clear of the numbers the other streaming tests put in the shared in-memory schedule.
    private const int ChannelNumber = 9601;
    private readonly MediaFixtures _media;

    public PlaceholderStreamTests(MediaFixtures media)
    {
        _media = media;
    }

    [Fact(Timeout = 240000)]
    public async Task ATuneInDuringAWindow_LoopsTheCard()
    {
        if (FfmpegHarness.Instance is not { } ff)
        {
            return;
        }

        await Task.Run(async () =>
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            var minute = (now.Hour * 60) + now.Minute;
            var block = new RatingBlock
            {
                IsPlaceholder = true,
                Period = RatingBlockPeriod.Custom,
                StartMinutes = (minute + 1439) % 1440,
                EndMinutes = (minute + 3) % 1440,
                PlaceholderName = "Off air",
                PlaceholderStyle = PlaceholderStyle.Static,
            };
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Placeholder join", Number = ChannelNumber, ShowBumpers = false, RatingBlocks = { block } };

            var service = NewService(ff, out var placeholders);
            await placeholders.EnsureAsync(channel, StreamSessionService.OutputFor(null).Width, null, CancellationToken.None).ConfigureAwait(false);
            Assert.NotNull(placeholders.TryGetCached(channel, block, StreamSessionService.OutputFor(null).Width, null));

            var loop = new ProgramLoop(new[] { Item("Placeholder item") }, DateTime.UtcNow.AddHours(-1));
            var log = _media.Output("placeholder.log");
            var output = _media.Output("placeholder.ts");
            await Stream(ff, service, channel, loop, log, output, "Placeholder: Off air\n$ ffmpeg", untilSeconds: 14).ConfigureAwait(false);

            var text = File.ReadAllText(log);
            Assert.DoesNotContain("Placeholder item\n$ ffmpeg", text, StringComparison.Ordinal);
            Assert.Contains("-stream_loop -1", text, StringComparison.Ordinal);
            Assert.True(ff.MeanLuma(ff.Frame(output, _media.Output("placeholder-card.png"), 12)) > 40);
            var video = ff.Packets(output, "v:0");
            for (var i = 1; i < video.Count; i++)
            {
                Assert.True(video[i].Dts > video[i - 1].Dts, $"video DTS went backwards at packet {i}");
            }
        });
    }

    [Fact(Timeout = 300000)]
    public async Task WhenTheWindowEnds_ContentResumesOnTime()
    {
        if (FfmpegHarness.Instance is not { } ff)
        {
            return;
        }

        await Task.Run(async () =>
        {
            // A window ending at the next minute (or the one after, when that is too close to see the card first).
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            var minute = (now.Hour * 60) + now.Minute;
            var end = (minute + (now.Second < 40 ? 1 : 2)) % 1440;
            var block = new RatingBlock
            {
                IsPlaceholder = true,
                Period = RatingBlockPeriod.Custom,
                StartMinutes = (minute + 1439) % 1440,
                EndMinutes = end,
                PlaceholderName = "Short break",
                PlaceholderStyle = PlaceholderStyle.Error,
            };
            var channel = new Channel { Id = Guid.NewGuid().ToString("N"), Name = "Placeholder resume", Number = ChannelNumber + 1, ShowBumpers = false, RatingBlocks = { block } };

            var service = NewService(ff, out var placeholders);
            await placeholders.EnsureAsync(channel, StreamSessionService.OutputFor(null).Width, null, CancellationToken.None).ConfigureAwait(false);

            var loop = new ProgramLoop(new[] { Item("Resume item") }, DateTime.UtcNow.AddHours(-1));
            var log = _media.Output("resume.log");
            var output = _media.Output("resume.ts");
            var windowLeft = (now.Date.AddMinutes(end < minute ? end + 1440 : end) - now).TotalSeconds;
            await Stream(ff, service, channel, loop, log, output, "Resume item\n$ ffmpeg", untilSeconds: windowLeft + 4).ConfigureAwait(false);

            var text = File.ReadAllText(log);
            Assert.True(text.IndexOf("Placeholder: Short break\n$ ffmpeg", StringComparison.Ordinal) < text.IndexOf("Resume item\n$ ffmpeg", StringComparison.Ordinal));
            Assert.True(ff.MeanLuma(ff.Frame(output, _media.Output("resume-content.png"), windowLeft + 3)) > 40);
            var video = ff.Packets(output, "v:0");
            for (var i = 1; i < video.Count; i++)
            {
                Assert.True(video[i].Dts > video[i - 1].Dts, $"video DTS went backwards at packet {i}");
            }
        });
    }

    private async Task Stream(FfmpegHarness ff, StreamSessionService service, Channel channel, ProgramLoop loop, string log, string output, string until, double untilSeconds)
    {
        var schedules = (ConcurrentDictionary<int, IReadOnlyList<ProgramEntry>>)typeof(ChannelService)
            .GetField("MemorySchedules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        schedules[channel.Number] = loop;
        try
        {
            using var cts = new CancellationTokenSource();
            var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using (stream.ConfigureAwait(false))
            {
                var run = service.StreamToAsync(channel, stream, cts.Token, new SessionStats { LogPath = log });
                for (var i = 0; i < 2400 && !(File.Exists(log) && File.ReadAllText(log).Contains(until, StringComparison.Ordinal)); i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                for (var i = 0; i < 240 && Covered(ff, output) < untilSeconds; i++)
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
            if (Environment.GetEnvironmentVariable("LIVECHANNELS_PLACEHOLDER_PREVIEW") is { Length: > 0 } keep && File.Exists(log))
            {
                Directory.CreateDirectory(keep);
                File.Copy(log, Path.Combine(keep, Path.GetFileName(log)), overwrite: true);
                File.Copy(output, Path.Combine(keep, Path.GetFileName(output)), overwrite: true);
            }
        }
    }

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
        File.Copy(_media["progressive_1080p.mkv"], path, overwrite: true);
        return new ProgramEntry(Guid.NewGuid(), title, null, TimeSpan.FromSeconds(6).Ticks, path)
        {
            ItemKind = "Movie",
            IsMovie = true,
            SourceHeight = 1080,
            SourceVideoCodec = "h264",
            SourceAudioCodec = "aac",
            SourceAudioChannels = 2,
        };
    }

    private StreamSessionService NewService(FfmpegHarness ff, out PlaceholderService placeholders)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ff.Ffmpeg);
        encoder.ProbePath.Returns(ff.Ffprobe);

        var options = new EncodingOptions { EnableHardwareEncoding = false };
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        var encoders = new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>());

        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_media.Output("ph-cache"));
        paths.DataPath.Returns(_media.Output("ph-data"));
        paths.LogDirectoryPath.Returns(_media.Output("ph-logs"));

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
        var bumpers = new BumperService(encoder, paths, Substitute.For<ILogger<BumperService>>());
        placeholders = new PlaceholderService(encoder, paths, Substitute.For<ILogger<PlaceholderService>>());
        return new StreamSessionService(encoder, channels, encoders, intros, paths, Substitute.For<ILogger<StreamSessionService>>(), bumpers, placeholders);
    }
}
