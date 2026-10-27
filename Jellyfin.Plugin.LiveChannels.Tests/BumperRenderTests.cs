using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Renders real Up Next bumpers with jellyfin-ffmpeg and checks what comes out: a 15-second, 30 fps, bt709 video at
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
        Assert.InRange(Duration(ffmpeg, output), 14.9, 15.1);
        Assert.Equal(string.Empty, ffmpeg.DecodeErrors(output));

        // The chord is there but quiet, and fades out to silence by the end.
        Assert.InRange(Volume(ffmpeg, output, 2, 2), -45, -24);
        var tail = Volume(ffmpeg, output, 14.6, 0.3);
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
            Assert.InRange(Duration(ffmpeg, output), 14.9, 15.1);
            Assert.Equal(1080, ffmpeg.Stream(output, "video")!.Value.GetProperty("height").GetInt32());
            Preview(ffmpeg, output, name);
        }
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
