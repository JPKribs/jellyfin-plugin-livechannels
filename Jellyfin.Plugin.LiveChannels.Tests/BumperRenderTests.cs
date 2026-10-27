using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Renders real Up Next bumpers with jellyfin-ffmpeg and checks what comes out: a 10-second, 30 fps, bt709 video at
/// the channel's frame size with the chord on a stereo track, for every artwork shape, with and without a logo,
/// and for titles long enough to need fitting. Set <c>LIVECHANNELS_BUMPER_PREVIEW</c> to a folder to also save a
/// still of each, for looking at. Skipped quietly without jellyfin-ffmpeg.
/// </summary>
public sealed class BumperRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lc-bumper-" + Guid.NewGuid().ToString("N")[..8]);

    public BumperRenderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("landscape", 1920, 1080, true)]
    [InlineData("portrait", 1000, 1500, true)]
    [InlineData("square", 1000, 1000, false)]
    [InlineData("none", 0, 0, false)]
    public async Task RendersAFifteenSecondBumperForEveryArtworkShape(string name, int artWidth, int artHeight, bool withLogo)
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var art = artWidth > 0 ? Image(ffmpeg, name + "-art.jpg", artWidth, artHeight) : null;
        var logo = withLogo ? Image(ffmpeg, "logo.png", 400, 200) : null;
        var program = Episode(art, "The Long Way Home", "Harbor Lights");
        var output = Path.Combine(_dir, name + ".mp4");

        Assert.True(await Service(ffmpeg).RenderAsync(Channel(), program, 1280, logo, output));
        Preview(ffmpeg, output, name);

        var video = ffmpeg.Stream(output, "video")!.Value;
        Assert.Equal(1280, video.GetProperty("width").GetInt32());
        Assert.Equal(720, video.GetProperty("height").GetInt32());
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal("30/1", video.GetProperty("r_frame_rate").GetString());
        Assert.Equal("bt709", video.GetProperty("color_space").GetString());
        var audio = ffmpeg.Stream(output, "audio")!.Value;
        Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        Assert.InRange(Duration(ffmpeg, output), 9.9, 10.1);
        Assert.Equal(string.Empty, ffmpeg.DecodeErrors(output));

        // The chord is there but quiet, and fades out to silence by the end.
        Assert.InRange(Volume(ffmpeg, output, 2, 2), -45, -24);
        var tail = Volume(ffmpeg, output, 9.6, 0.3);
        Assert.True(tail < -60, "the chord should have faded out by the end, but is at " + tail + " dB");

        // The scene starts and ends black, and is lit in between.
        var start = ffmpeg.MeanLuma(ffmpeg.Frame(output, Path.Combine(_dir, name + "-start.png"), 0));
        var middle = ffmpeg.MeanLuma(ffmpeg.Frame(output, Path.Combine(_dir, name + "-mid.png"), 6));
        Assert.True(start < 20, "the bumper should open on black, but its first frame's mean luma is " + start);
        Assert.True(middle > start + 10, "the bumper should be lit mid-way, but its mean luma is " + middle);
    }

    [Fact]
    public async Task RendersLongTitlesAMovieAndAVideo()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var service = Service(ffmpeg);
        var art = Image(ffmpeg, "wide.jpg", 1920, 1080);
        var poster = Image(ffmpeg, "poster.jpg", 1000, 1500);
        var cases = new (string Name, ProgramEntry Program)[]
        {
            ("long", Episode(art, "The Extraordinary and Mostly True Adventures of Captain Bartholomew Finch and the Lighthouse Society", "In Which Our Hero Learns the Lighthouse Keeper Was Never There at All")),
            ("movie", Movie(poster)),
            ("video", Video()),
        };

        foreach (var (name, program) in cases)
        {
            var output = Path.Combine(_dir, name + ".mp4");
            Assert.True(await service.RenderAsync(Channel(), program, 1920, null, output), name);
            Assert.InRange(Duration(ffmpeg, output), 9.9, 10.1);
            Assert.Equal(1080, ffmpeg.Stream(output, "video")!.Value.GetProperty("height").GetInt32());
            Preview(ffmpeg, output, name);
        }
    }

    [Theory]
    [InlineData("then-landscape", 1920, 1080, "Northbound")]
    [InlineData("then-portrait", 1000, 1500, "Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb")]
    public async Task ACardPlays_WithItsThenLineDrawnOn(string name, int artWidth, int artHeight, string next)
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var service = Service(ffmpeg);
        var program = Episode(Image(ffmpeg, name + ".jpg", artWidth, artHeight), "The Long Way Home", "Harbor Lights");
        var card = Path.Combine(_dir, name + ".mp4");
        Assert.True(await service.RenderAsync(Channel(), program, 1280, null, card));

        // Without a Then line the card plays with its room empty; with one, the line is drawn where it was left.
        var overlay = service.ThenOverlay(card, program, 1280, new BumperThen(next, "9:45 PM"));
        Assert.NotNull(overlay);
        Assert.Contains("drawtext=", overlay, StringComparison.Ordinal);
        Assert.EndsWith(",", overlay, StringComparison.Ordinal);

        // Played through the stream's own argument builder, once without the line and once with it: the line adds
        // light text where the card left a dark gap, after it has faded in, and nothing before.
        var software = new Jellyfin.Plugin.LiveChannels.Models.VideoEncoderProfile("libx264", "H.264", false, Array.Empty<string>(), ["-preset", "ultrafast"], "format=yuv420p", false);
        string Play(string? filter, string file)
        {
            var path = Path.Combine(_dir, file);
            ffmpeg.RunProducer(StreamArguments.Build(card, TimeSpan.Zero, TimeSpan.Zero, 1280, 4000, software, "aac", 128, null, softwareDecode: true, inputMatrix: "bt709", overlayFilter: filter), path);
            return path;
        }

        var bare = Play(null, name + "-bare.ts");
        var drawn = Play(overlay, name + "-drawn.ts");
        double Luma(string video, double at, string tag) => ffmpeg.MeanLuma(ffmpeg.Frame(video, Path.Combine(_dir, name + "-" + tag + ".png"), at));
        Assert.True(Luma(drawn, 8, "drawn8") > Luma(bare, 8, "bare8") + 0.05, "the Then line should be drawn by 8s");
        Assert.True(Math.Abs(Luma(drawn, 1, "drawn1") - Luma(bare, 1, "bare1")) < 0.05, "the Then line should not show before it fades in");
        Preview(ffmpeg, drawn, name);
    }

    [Fact]
    public async Task TheBlackFillerIsSilentBlackAndLongerThanABumper()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var black = await Service(ffmpeg).EnsureBlackAsync(1280, default);
        Assert.NotNull(black);
        Assert.True(Duration(ffmpeg, black!) > BumperService.BumperLength.TotalSeconds);
        Assert.True(ffmpeg.MeanLuma(ffmpeg.Frame(black!, Path.Combine(_dir, "black.png"), 5)) < 20);
        Assert.True(Volume(ffmpeg, black!, 1, 5) < -80);
    }

    [Fact]
    public async Task TheChannelLayer_IsRenderedAheadOfTime_AndAnItemCardThenOnlyAddsTheItem()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var service = Service(ffmpeg);
        string[] Layers() => Directory.GetFiles(Path.Combine(_dir, "cache"), "layer-*", SearchOption.AllDirectories);
        Assert.True(await service.PrepareLayerAsync(Channel(), 1280, null));
        var layer = Assert.Single(Layers());

        // Mark the layer (an mp4 ignores trailing bytes): a re-render would replace the file and lose the mark, which
        // timestamps cannot show, since a reused layer is touched and Linux keeps no reliable creation time.
        var mark = "livechannels-layer-mark"u8.ToArray();
        await using (var file = new FileStream(layer, FileMode.Append))
        {
            await file.WriteAsync(mark);
        }

        // A card built afterwards reuses the layer rather than rendering it again.
        var output = Path.Combine(_dir, "after-layer.mp4");
        Assert.True(await service.RenderAsync(Channel(), Episode(null, "Series", "Pilot"), 1280, null, output));
        Assert.Equal(layer, Assert.Single(Layers()));
        Assert.True(File.ReadAllBytes(layer).AsSpan().EndsWith(mark));

        // Reset clears the layers so the task renders them afresh.
        service.ClearLayers();
        Assert.Empty(Layers());
    }

    [Fact]
    public async Task PreparedBumpersAreServedFromTheCache()
    {
        if (FfmpegHarness.Instance is not { } ffmpeg)
        {
            return;
        }

        var service = Service(ffmpeg);
        var program = Episode(null, "Show", "Episode");
        Assert.Null(service.TryGetCached(Channel(), program, 1280, null));

        service.Prepare(Channel(), program, 1280, null);
        string? cached = null;
        for (var i = 0; i < 600 && cached is null; i++)
        {
            await Task.Delay(100);
            cached = service.TryGetCached(Channel(), program, 1280, null);
        }

        Assert.NotNull(cached);

        // A change to anything the card shows is a different bumper.
        var renamed = Episode(null, "Show", "Another Episode");
        Assert.Null(service.TryGetCached(Channel(), renamed, 1280, null));
    }

    private static Channel Channel() => new() { Id = "c1", Name = "Test", Number = 7 };

    private static ProgramEntry Episode(string? art, string series, string name)
        => new(Guid.NewGuid(), series + " - " + name, null, TimeSpan.FromMinutes(44).Ticks, "/media/e.mkv")
        {
            ItemKind = "Episode",
            SeriesId = Guid.NewGuid(),
            SeriesName = series,
            RawName = name,
            SeasonNumber = 2,
            EpisodeNumber = 5,
            Year = 2019,
            Genres = ["Drama"],
            CommunityRating = 8.4f,
            SeriesOfficialRating = "TV-14",
            ThumbImagePath = art,
        };

    private static ProgramEntry Movie(string poster)
        => new(Guid.NewGuid(), "The Quiet Hours", null, TimeSpan.FromMinutes(106).Ticks, "/media/m.mkv")
        {
            ItemKind = "Movie",
            IsMovie = true,
            RawName = "The Quiet Hours",
            Tagline = "Nobody sleeps in this town.",
            Year = 2018,
            Genres = ["Horror", "Mystery"],
            CommunityRating = 6.9f,
            OfficialRating = "R",
            PrimaryImagePath = poster,
        };

    private static ProgramEntry Video()
        => new(Guid.NewGuid(), "Lake Trip 2024", null, TimeSpan.FromMinutes(12).Ticks, "/media/v.mp4")
        {
            ItemKind = "Video",
            RawName = "Lake Trip 2024",
            PremiereDate = new DateTime(2024, 6, 14, 0, 0, 0, DateTimeKind.Utc),
        };

    // The mean volume of just this stretch: trimmed in the filter, since an output seek still feeds everything
    // before it through the audio filters.
    private static double Volume(FfmpegHarness ffmpeg, string path, double from, double seconds)
    {
        var (_, _, err) = FfmpegHarness.Run(ffmpeg.Ffmpeg, ["-hide_banner", "-i", path, "-vn", "-af", "atrim=start=" + from.ToString(CultureInfo.InvariantCulture) + ":duration=" + seconds.ToString(CultureInfo.InvariantCulture) + ",volumedetect", "-f", "null", "-"]);
        var match = System.Text.RegularExpressions.Regex.Match(err, @"mean_volume: (-?[\d.]+) dB");
        Assert.True(match.Success, err);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static double Duration(FfmpegHarness ffmpeg, string path)
    {
        var (_, stdout, _) = FfmpegHarness.Run(ffmpeg.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static void Preview(FfmpegHarness ffmpeg, string output, string name)
    {
        if (Environment.GetEnvironmentVariable("LIVECHANNELS_BUMPER_PREVIEW") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var at in new[] { 1.4, 3.0, 8.0 })
            {
                ffmpeg.Frame(output, Path.Combine(folder, name + "-" + at.ToString("0.0", CultureInfo.InvariantCulture) + ".png"), at);
            }

            File.Copy(output, Path.Combine(folder, name + ".mp4"), overwrite: true);
        }
    }

    private string Image(FfmpegHarness ffmpeg, string name, int width, int height)
    {
        var path = Path.Combine(_dir, name);
        ffmpeg.Exec("-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=" + width + "x" + height + ":d=1", "-frames:v", "1", path);
        return path;
    }

    private BumperService Service(FfmpegHarness ffmpeg)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg.Ffmpeg);
        encoder.ProbePath.Returns(ffmpeg.Ffprobe);
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(Path.Combine(_dir, "cache"));
        paths.DataPath.Returns(Path.Combine(_dir, "data"));
        return new BumperService(encoder, paths, NullLogger<BumperService>.Instance);
    }
}
