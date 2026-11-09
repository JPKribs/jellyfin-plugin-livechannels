using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the placeholder card's render arguments (<see cref="PlaceholderArguments"/>).
/// </summary>
public class PlaceholderCardTests
{
    [Fact]
    public void TheMessage_FillsInTheResumeTimeAndContent()
    {
        var text = PlaceholderArguments.ResolveMessage(PlaceholderArguments.DefaultBody, new DateTime(2026, 11, 8, 18, 30, 0), "The Office", CultureInfo.InvariantCulture);

        Assert.Equal("We will resume at 18:30 with The Office.", text);
    }

    [Fact]
    public void AGifOrVideo_IsReadWithItsFormatNamed_AndRunsTheCardsLength()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-ph-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "clip.bin");
            File.WriteAllBytes(video, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m']);
            var text = Path.Combine(dir, "notes.txt");
            File.WriteAllText(text, "not a picture");

            Assert.Equal(PlaceholderMediaKind.Video, PlaceholderArguments.MediaKindOf(video));
            Assert.Null(PlaceholderArguments.MediaKindOf(text));
            Assert.Equal(PlaceholderStyle.Default, PlaceholderArguments.EffectiveStyle(PlaceholderStyle.Custom, text));

            var layers = PlaceholderArguments.Layers(PlaceholderStyle.Custom, Path.Combine(dir, "p"), 1920, 1080);
            var args = PlaceholderArguments.BuildSourceArguments(layers, null, video, null, null, "/tmp/out.mp4");
            Assert.Equal("mov", args[args.IndexOf("-stream_loop") - 1]);
            Assert.Equal("10", args[args.IndexOf("-t") + 1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheMenu_StartsWithDefault_AndEndsWithCustom()
    {
        var styles = Enum.GetValues<PlaceholderStyle>();

        Assert.Equal(PlaceholderStyle.Default, styles[0]);
        Assert.Equal(PlaceholderStyle.Custom, styles[^1]);
    }

    [Fact]
    public void EveryStyle_EndsInSquarePixels()
    {
        foreach (var style in Enum.GetValues<PlaceholderStyle>())
        {
            var layers = PlaceholderArguments.Layers(PlaceholderArguments.EffectiveStyle(style, null), "/tmp/p", 1280, 720);
            var args = PlaceholderArguments.BuildSceneArguments(layers, null, null, true, true, "/tmp/out.mp4");

            var filter = args[args.IndexOf("-filter_complex") + 1];

            Assert.Contains("setsar=1,format=yuv420p[v]", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryStyle_RendersSquarePixelsAtFullWidth()
    {
        var ffmpeg = FfmpegHarness.Instance;
        if (ffmpeg is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-sar-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var style in Enum.GetValues<PlaceholderStyle>().Where(s => s != PlaceholderStyle.Custom))
            {
                var layers = PlaceholderArguments.Layers(style, Path.Combine(dir, style.ToString()), 1280, 720);
                if (PlaceholderArguments.BuildPrepassArguments(layers) is { } prepass)
                {
                    ffmpeg.Exec(prepass.ToArray());
                }

                var output = Path.Combine(dir, style + ".mp4");
                ffmpeg.Exec(PlaceholderArguments.BuildSceneArguments(layers, null, null, true, true, output).ToArray());

                var video = ffmpeg.Stream(output, "video")!.Value;
                Assert.True(video.GetProperty("width").GetInt32() == 1280, style + " width");
                Assert.True(video.GetProperty("sample_aspect_ratio").GetString() == "1:1", style + " sample aspect " + video.GetProperty("sample_aspect_ratio").GetString());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoStyle_FadesInOrOut_SoTheClipLoops()
    {
        foreach (var style in Enum.GetValues<PlaceholderStyle>())
        {
            var layers = PlaceholderArguments.Layers(PlaceholderArguments.EffectiveStyle(style, null), "/tmp/p", 1920, 1080);
            var args = PlaceholderArguments.BuildSourceArguments(layers, null, null, null, null, "/tmp/out.mp4");

            var filter = args[args.IndexOf("-filter_complex") + 1];

            Assert.DoesNotContain("fade=", filter, StringComparison.Ordinal);
            Assert.Equal("10", args[args.IndexOf("-t") + 1]);
        }
    }

    [Fact]
    public async Task TheCardsTask_RendersEachScene_Once_OnlyForChannelsWithPlaceholders_AndDropsOnesNoBlockUses()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-phsvc-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = NewService(ffmpeg, dir);
            var block = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 60, EndMinutes = 120, PlaceholderStyle = PlaceholderStyle.Static };
            var twin = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 180, EndMinutes = 240, PlaceholderStyle = PlaceholderStyle.Static };
            var channel = new Channel { Id = "c", Name = "Card", Number = 9, RatingBlocks = { block, twin } };

            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            var scene = service.TryGetCached(channel, block, 640, null)!.Value;
            Assert.Equal(PlaceholderStyle.Static, scene.Style);
            Assert.Equal(scene, service.TryGetCached(channel, twin, 640, null));
            Assert.InRange(Duration(ffmpeg, scene.Path), 9.9, 10.1);

            // A new look renders a new scene; the old one stays for a session still showing it, until it has gone
            // unused for days.
            block.PlaceholderStyle = PlaceholderStyle.Error;
            twin.PlaceholderStyle = PlaceholderStyle.Error;
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            var error = service.TryGetCached(channel, block, 640, null)!.Value;
            Assert.Equal(PlaceholderStyle.Error, error.Style);
            Assert.True(File.Exists(scene.Path));
            Age(Path.GetDirectoryName(scene.Path)!, scene.Path);
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            Assert.False(File.Exists(scene.Path));
            Assert.True(File.Exists(error.Path));

            // A channel without placeholder blocks renders nothing, and only drops scenes long unused.
            channel.RatingBlocks.Clear();
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            Assert.True(File.Exists(error.Path));
            Age(Path.GetDirectoryName(error.Path)!, null);
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(error.Path)!));

            var plain = new Channel { Id = "p", Name = "Plain", Number = 10 };
            await service.EnsureAsync(plain, 640, null, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(error.Path)!)!, "ch10")));

            channel.RatingBlocks.Add(block);
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            service.Clear();
            Assert.Null(service.TryGetCached(channel, block, 640, null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ACustomCard_ShowsTheDefaultLook_UntilItsFileTurnsUp()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-phcustom-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = NewService(ffmpeg, dir);
            var image = Path.Combine(dir, "card.png");
            var block = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 60, EndMinutes = 120, PlaceholderStyle = PlaceholderStyle.Custom, PlaceholderMediaPath = image };
            var channel = new Channel { Id = "c", Name = "Card", Number = 9, RatingBlocks = { block } };

            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            Assert.Equal(PlaceholderStyle.Default, service.TryGetCached(channel, block, 640, null)?.Style);

            Assert.Equal(0, FfmpegHarness.Run(ffmpeg, ["-y", "-f", "lavfi", "-i", "color=c=red:s=320x180", "-frames:v", "1", image]).ExitCode);
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            Assert.Equal(PlaceholderStyle.Custom, service.TryGetCached(channel, block, 640, null)?.Style);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFile_IsOnlyAVideo_WhenItsBytesSaySo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-phkind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var g = Path.Combine(dir, "notes.txt");
            File.WriteAllText(g, "Good evening, this is not a video" + new string('.', 400));
            Assert.Null(PlaceholderArguments.MediaKindOf(g));

            var ts = Path.Combine(dir, "clip.ts");
            var packets = new byte[188 * 3];
            packets[0] = packets[188] = packets[376] = 0x47;
            File.WriteAllBytes(ts, packets);
            Assert.Equal(PlaceholderMediaKind.Video, PlaceholderArguments.MediaKindOf(ts));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ACustomImage_WithAPercentInItsPath_StillRenders()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-phpct-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = NewService(ffmpeg, dir);
            Directory.CreateDirectory(dir);
            var image = Path.Combine(dir, "100%_off%03d.png");
            Assert.Equal(0, FfmpegHarness.Run(ffmpeg, ["-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180", "-frames:v", "1", "-update", "1", image]).ExitCode);
            var block = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 60, EndMinutes = 120, PlaceholderStyle = PlaceholderStyle.Custom, PlaceholderMediaPath = image };
            var channel = new Channel { Id = "c", Name = "Card", Number = 9, RatingBlocks = { block } };

            await service.EnsureAsync(channel, 640, null, CancellationToken.None);

            Assert.Equal(PlaceholderStyle.Custom, service.TryGetCached(channel, block, 640, null)?.Style);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionsDrawingTheSameMessage_AtOnce_AllReadItWhole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-phrace-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = NewService("/nonexistent/ffmpeg", dir);
            var block = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 60, EndMinutes = 120, PlaceholderTitle = new string('x', 150) };
            var overlays = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => service.TextOverlay(block, 1280, null, DateTime.Now, "Heat", null))));

            foreach (var overlay in overlays)
            {
                var file = Regex.Match(overlay!, "textfile='([^']+)'").Groups[1].Value;
                Assert.StartsWith("xxx", File.ReadAllText(file), StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Regex.Match(overlays[0]!, "textfile='([^']+)'").Groups[1].Value)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ALongMessage_IsDrawnSmaller_ThenCutShort_ToFitTheFrame()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-phfit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var metrics = BumperFonts.Extract(dir, null)!.Metrics(BumperFace.Medium);

            Assert.Equal(("Short", 40), PlaceholderService.Fit("Short", metrics, 40, 1000));
            var longer = new string('W', 30);
            var (text, size) = PlaceholderService.Fit(longer, metrics, 40, 1000);
            Assert.Equal(longer, text);
            Assert.True(size < 40 && metrics.Measure(text, size) <= 1000);
            var (cut, smallest) = PlaceholderService.Fit(new string('W', 200), metrics, 40, 1000);
            Assert.EndsWith("\u2026", cut, StringComparison.Ordinal);
            var (emoji, _) = PlaceholderService.Fit(string.Concat(Enumerable.Repeat("W\U0001F600", 120)), metrics, 40, 1000);
            Assert.False(char.IsHighSurrogate(emoji[^2]));
            var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
            var (joined, _) = PlaceholderService.Fit(string.Concat(Enumerable.Repeat("WW" + family, 80)), metrics, 40, 1000);
            Assert.True(joined.EndsWith("WW\u2026", StringComparison.Ordinal) || joined.EndsWith(family + "\u2026", StringComparison.Ordinal) || joined.EndsWith("W\u2026", StringComparison.Ordinal));
            Assert.Equal(22, smallest);
            Assert.True(metrics.Measure(cut, smallest) <= 1000);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheMessage_IsDrawnOnWhileTheScenePlays_WithTheResumeTimeAndContent()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "lc-phtext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = NewService(ffmpeg, dir);
            var block = new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = 60, EndMinutes = 120 };
            var channel = new Channel { Id = "c", Name = "Card", Number = 9, RatingBlocks = { block } };
            await service.EnsureAsync(channel, 640, null, CancellationToken.None);
            var scene = service.TryGetCached(channel, block, 640, null)!.Value;

            var overlay = service.TextOverlay(block, 640, null, new DateTime(2026, 11, 8, 18, 30, 0), "The Office", scene.Style);
            Assert.NotNull(overlay);
            var files = Regex.Matches(overlay!, "textfile='([^']+)'").Select(m => File.ReadAllText(m.Groups[1].Value)).ToList();
            Assert.Equal(PlaceholderArguments.DefaultTitle, files[0]);
            Assert.Contains("with The Office.", files[1], StringComparison.Ordinal);

            var run = FfmpegHarness.Run(ffmpeg, ["-v", "error", "-i", scene.Path, "-vf", overlay + "null", "-frames:v", "5", "-f", "null", "-"]);
            Assert.True(run.ExitCode == 0, run.StdErr);

            var titleOnly = service.TextOverlay(block, 640, null, DateTime.Now, null, scene.Style);
            Assert.Single(Regex.Matches(titleOnly!, "drawtext="));
            Assert.Equal(Regex.Match(overlay!, ":y=([0-9]+)").Value, Regex.Match(titleOnly!, ":y=([0-9]+)").Value);

            block.PlaceholderBody = "Back at {time}";
            Assert.Equal(2, Regex.Matches(service.TextOverlay(block, 640, null, DateTime.Now, null, scene.Style)!, "drawtext=").Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Backdates every file in a folder (or just one) past the scene retention, as if unused for days.
    private static void Age(string directory, string? only)
    {
        foreach (var file in only is null ? Directory.GetFiles(directory) : [only, Path.ChangeExtension(only, ".json")])
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-4));
        }
    }

    private static PlaceholderService NewService(string ffmpeg, string dir)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg);
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(Path.Combine(dir, "cache"));
        paths.DataPath.Returns(Path.Combine(dir, "data"));
        return new PlaceholderService(encoder, paths, NullLogger<PlaceholderService>.Instance);
    }

    private static string? SystemFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("LIVECHANNELS_TEST_FFMPEG") is { Length: > 0 } configured ? configured : FfmpegHarness.Instance?.Ffmpeg ?? "/usr/bin/ffmpeg";
        return File.Exists(path) ? path : null;
    }

    private static double Duration(string ffmpeg, string file)
    {
        var probe = FfmpegHarness.Run(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]);
        return double.Parse(probe.StdOut.Trim(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task RenderPreviews_WhenAFolderIsGiven()
    {
        if (Environment.GetEnvironmentVariable("LIVECHANNELS_PLACEHOLDER_PREVIEW") is not { Length: > 0 } folder)
        {
            return;
        }

        var ffmpeg = Environment.GetEnvironmentVariable("LIVECHANNELS_TEST_FFMPEG") is { Length: > 0 } path ? path : "/usr/bin/ffmpeg";
        var width = int.TryParse(Environment.GetEnvironmentVariable("LIVECHANNELS_PLACEHOLDER_WIDTH"), out var w) ? w : 1920;
        var height = width * 9 / 16;
        Directory.CreateDirectory(folder);
        var work = Path.Combine(folder, "work");
        Directory.CreateDirectory(work);

        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg);
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(Path.Combine(work, "cache"));
        paths.DataPath.Returns(Path.Combine(work, "data"));
        var logos = new DefaultLogoService(encoder, paths, NullLogger<DefaultLogoService>.Instance);
        var logo = await logos.GetAsync(7, "Sitcom Central", LogoStyle.Number, string.Empty, true, CancellationToken.None);
        var logoPath = Path.Combine(work, "logo.png");
        await File.WriteAllBytesAsync(logoPath, logo!);

        var fonts = BumperFonts.Extract(Path.Combine(work, "fonts"), null)!;
        var titleFile = Path.Combine(work, "title.txt");
        var bodyFile = Path.Combine(work, "body.txt");
        await File.WriteAllTextAsync(titleFile, PlaceholderArguments.DefaultTitle);
        await File.WriteAllTextAsync(bodyFile, PlaceholderArguments.ResolveMessage(PlaceholderArguments.DefaultBody, new DateTime(2026, 11, 8, 18, 30, 0), "The Office", new CultureInfo("en-US")));
        var title = (fonts.For(BumperFace.DisplayBold, PlaceholderArguments.DefaultTitle).Path, titleFile);
        var body = (fonts.For(BumperFace.Medium, "x").Path, bodyFile);

        var image = Path.Combine(work, "custom.png");
        Assert.Equal(0, FfmpegHarness.Run(ffmpeg, ["-y", "-f", "lavfi", "-i", "mandelbrot=s=1280x720:end_pts=1", "-frames:v", "1", image]).ExitCode);
        var gif = Path.Combine(work, "custom.gif");
        Assert.Equal(0, FfmpegHarness.Run(ffmpeg, ["-y", "-f", "lavfi", "-i", "life=s=160x90:mold=10:r=10:ratio=0.1:death_color=#202060:life_color=#40ff90,scale=480:270:flags=neighbor", "-t", "10", gif]).ExitCode);
        var video = Path.Combine(work, "custom.mp4");
        Assert.Equal(0, FfmpegHarness.Run(ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", video]).ExitCode);
        var cards = Enum.GetValues<PlaceholderStyle>().Where(s => s != PlaceholderStyle.Custom).Select(s => (s.ToString().ToLowerInvariant(), s, (string?)null))
            .Concat([("custom-image", PlaceholderStyle.Custom, image), ("custom-gif", PlaceholderStyle.Custom, gif), ("custom-video", PlaceholderStyle.Custom, video)]);
        var only = Environment.GetEnvironmentVariable("LIVECHANNELS_PLACEHOLDER_ONLY");
        foreach (var (name, style, file) in cards.Where(c => string.IsNullOrEmpty(only) || only.Split(',').Contains(c.Item1)))
        {
            var layers = PlaceholderArguments.Layers(PlaceholderArguments.EffectiveStyle(style, file), Path.Combine(work, name), width, height);
            if (PlaceholderArguments.BuildPrepassArguments(layers) is { } prepass)
            {
                var pre = FfmpegHarness.Run(ffmpeg, prepass, timeoutSeconds: 600);
                Assert.True(pre.ExitCode == 0, style + " prepass: " + pre.StdErr);
            }

            var output = Path.Combine(folder, name + ".mp4");
            var args = PlaceholderArguments.BuildSourceArguments(layers, logoPath, file, title, body, output);
            var started = DateTime.UtcNow;
            var run = FfmpegHarness.Run(ffmpeg, args, timeoutSeconds: 900);
            Assert.True(run.ExitCode == 0, name + ": " + run.StdErr);
            await File.AppendAllTextAsync(Path.Combine(folder, "timings.txt"), name + " " + (DateTime.UtcNow - started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s\n");
        }
    }
}
