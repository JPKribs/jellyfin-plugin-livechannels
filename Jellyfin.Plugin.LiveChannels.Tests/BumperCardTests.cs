using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for what an Up Next bumper says (<see cref="BumperContent"/>), how its text is fitted and laid out
/// (<see cref="BumperLayout"/>, measured with the bundled fonts through <see cref="FontMetrics"/>), and the ffmpeg
/// arguments that render it (<see cref="BumperArguments"/>).
/// </summary>
public sealed class BumperCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lc-bumper-card-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly BumperFonts _fonts;

    public BumperCardTests()
    {
        _fonts = BumperFonts.Extract(Path.Combine(_dir, "fonts"), null) ?? throw new InvalidOperationException("the bundled fonts did not load");
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

    [Fact]
    public void AnEpisode_ShowsItsSeriesEpisodeAndItsSeriesRating()
    {
        var content = BumperContent.For(Episode());

        Assert.Equal("The Long Way Home", content.Title);
        Assert.Equal("S2 · E5", content.Tag);
        Assert.Equal("Harbor Lights", content.Subtitle);
        Assert.False(content.SubtitleIsTagline);
        Assert.Equal("2019 · Drama · 44 min", content.Details);
        Assert.Equal("8.4", content.Score);
        Assert.Equal("TV-14", content.Rating);
    }

    [Fact]
    public void AnEpisodeWithoutASeriesRating_FallsBackToItsOwn_AndUnratedHasNoBadge()
    {
        Assert.Equal("TV-PG", BumperContent.For(Episode(seriesRating: null, ownRating: "TV-PG")).Rating);
        Assert.Null(BumperContent.For(Episode(seriesRating: null, ownRating: null)).Rating);
        Assert.Null(BumperContent.For(Episode(seriesRating: "  ", ownRating: null)).Rating);
    }

    [Theory]
    [InlineData(0, 3, "Special")]
    [InlineData(0, null, "Special")]
    [InlineData(2, 5, "S2 · E5")]
    [InlineData(null, 5, "E5")]
    [InlineData(3, null, null)]
    [InlineData(null, null, null)]
    public void TheEpisodeTag_ShowsSpecialsAndTheFirstEpisodeNumber(int? season, int? episode, string? expected)
        => Assert.Equal(expected, BumperContent.EpisodeTag(season, episode));

    [Fact]
    public void AMovie_ShowsItsTaglineInItalics()
    {
        var movie = new ProgramEntry(Guid.NewGuid(), "Northbound", null, TimeSpan.FromMinutes(118).Ticks, "/m.mkv")
        {
            ItemKind = "Movie",
            IsMovie = true,
            RawName = "Northbound",
            Tagline = "  Some roads\nonly go one way. ",
            Year = 2021,
            Genres = ["Thriller", "Drama"],
            OfficialRating = "PG-13",
        };

        var content = BumperContent.For(movie);
        Assert.Equal("Northbound", content.Title);
        Assert.Null(content.Tag);
        Assert.Equal("Some roads only go one way.", content.Subtitle);
        Assert.True(content.SubtitleIsTagline);
        Assert.Equal("2021 · Thriller, Drama · 1 h 58 min", content.Details);
        Assert.Null(content.Score);
        Assert.Equal("PG-13", content.Rating);
    }

    [Fact]
    public void AVideo_ShowsItsTitleDateAndRuntimeOnly()
    {
        var video = new ProgramEntry(Guid.NewGuid(), "Lake Trip 2024", null, TimeSpan.FromMinutes(12).Ticks, "/v.mp4")
        {
            ItemKind = "Video",
            RawName = "Lake Trip 2024",
            PremiereDate = new DateTime(2024, 6, 14, 0, 0, 0, DateTimeKind.Utc),
            Genres = ["Family"],
        };

        var content = BumperContent.For(video);
        Assert.Equal("Lake Trip 2024", content.Title);
        Assert.Null(content.Tag);
        Assert.Null(content.Subtitle);
        Assert.Equal("June 14, 2024 · 12 min", content.Details);
        Assert.Null(content.Rating);
    }

    [Theory]
    [InlineData(44, "44 min")]
    [InlineData(60, "1 h")]
    [InlineData(112, "1 h 52 min")]
    [InlineData(0.4, null)]
    public void Runtimes_ReadNaturally(double minutes, string? expected)
        => Assert.Equal(expected, BumperContent.Runtime(TimeSpan.FromMinutes(minutes).Ticks));

    [Fact]
    public void TheBundledFonts_MeasureTextAndKnowTheirGlyphs()
    {
        var inter = _fonts.Metrics(BumperFace.Regular);
        Assert.True(inter.Covers("★ 8.4 / 10 · …"));
        Assert.False(inter.Covers("東京"));
        Assert.True(inter.Measure("Harbor Lights", 28) > inter.Measure("Harbor", 28));
        Assert.Equal(inter.Measure("Harbor", 56), 2 * inter.Measure("Harbor", 28), 6);
        Assert.True(inter.Ascent(28) > 0 && inter.Descent(28) > 0);
        Assert.Null(FontMetrics.Parse(new byte[64]));
    }

    [Fact]
    public void Wrap_BreaksAtWordsIntoTwoLines_AndEllipsizesTheRest()
    {
        var metrics = _fonts.Metrics(BumperFace.DisplayBold);
        const string Title = "The Extraordinary and Mostly True Adventures of Captain Bartholomew Finch";

        var lines = BumperLayout.Wrap(Title, metrics, 46, 520, 2);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(metrics.Measure(l, 46) <= 520, l));
        Assert.StartsWith("The Extraordinary", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("…", lines[1], StringComparison.Ordinal);

        // Short text is left alone.
        Assert.Equal(["Lost"], BumperLayout.Wrap("Lost", metrics, 46, 520, 2));
    }

    [Fact]
    public void Wrap_BreaksAWordTooLongForTheLine()
    {
        var metrics = _fonts.Metrics(BumperFace.DisplayBold);
        var lines = BumperLayout.Wrap(new string('W', 60), metrics, 46, 300, 2);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(metrics.Measure(l, 46) <= 300, l));
    }

    [Fact]
    public void Ellipsize_FitsTheRoom()
    {
        var metrics = _fonts.Metrics(BumperFace.Medium);
        const string Name = "In Which Our Hero Learns the Lighthouse Keeper Was Never There at All";

        var fitted = BumperLayout.Ellipsize(Name, metrics, 28, 400);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);
        Assert.True(metrics.Measure(fitted, 28) <= 400);
        Assert.Equal("Harbor Lights", BumperLayout.Ellipsize("Harbor Lights", metrics, 28, 400));
        Assert.Equal(string.Empty, BumperLayout.Ellipsize(Name, metrics, 28, 2));
    }

    [Theory]
    [InlineData(1920, 1080, "Landscape")]
    [InlineData(1000, 1500, "Portrait")]
    [InlineData(1000, 1000, "Square")]
    [InlineData(1100, 1000, "Square")]
    [InlineData(0, 100, "None")]
    public void TheArtworkFrame_TakesTheImagesShape(int width, int height, string expected)
        => Assert.Equal(Enum.Parse<ArtShape>(expected), BumperLayout.ShapeFor(width, height));

    [Theory]
    [InlineData("Landscape", 560, 315)]
    [InlineData("Portrait", 316, 474)]
    [InlineData("Square", 420, 420)]
    public void TheArtwork_SitsOnTheRight_AtTheSameMarginForEveryShape(string shape, int width, int height)
    {
        var layout = BumperLayout.Compute(BumperContent.For(Episode()), Enum.Parse<ArtShape>(shape), 1280, 720, _fonts);

        var art = layout.Art!.Value;
        Assert.Equal(1280 - 72, art.X + art.Width);
        Assert.Equal((width, height), (art.Width, art.Height));
        Assert.Equal(132 + ((492 - height) / 2), art.Y);

        // The info column starts at the left margin and stays clear of the artwork.
        Assert.All(layout.Rows.SelectMany(r => r.Texts), t => Assert.True(t.X >= 72));
        Assert.Equal(72, layout.Rows[0].Texts[0].X);
    }

    [Fact]
    public void TheRows_SlideInInTurn_AndStackDownTheColumn()
    {
        var layout = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts);

        Assert.Equal(4, layout.Rows.Count);
        Assert.Equal([1.0, 1.3, 1.6, 1.9], layout.Rows.Select(r => r.Delay));
        var baselines = layout.Rows.Select(r => r.Texts[0].Baseline).ToList();
        Assert.Equal(baselines.OrderBy(b => b), baselines);

        // The tag sits in its pill, with the episode name after it.
        var tagRow = layout.Rows[1];
        var pill = tagRow.Pill!.Value;
        Assert.Equal("S2 · E5", tagRow.Texts[0].Text);
        Assert.True(tagRow.Texts[1].X > pill.X + pill.Width);
    }

    [Fact]
    public void TheRatingBadge_SitsTopRight_AndIsLeftOutWhenUnrated()
    {
        var rated = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts);
        var badge = rated.Badge!.Value;
        Assert.Equal(1280 - 36, badge.X + badge.Width);
        Assert.Equal(36, badge.Y);
        Assert.Equal(52, badge.Height);
        Assert.Equal("TV-14", rated.BadgeText!.Value.Text);

        var unrated = BumperLayout.Compute(BumperContent.For(Episode(seriesRating: null, ownRating: null)), ArtShape.Landscape, 1280, 720, _fonts);
        Assert.Null(unrated.Badge);
        Assert.Null(unrated.BadgeText);
    }

    [Fact]
    public void ALongTitle_DropsToTheSmallerSize_OverTwoLines()
    {
        var content = BumperContent.For(Episode(series: "The Extraordinary and Mostly True Adventures of Captain Bartholomew Finch and the Lighthouse Society"));
        var layout = BumperLayout.Compute(content, ArtShape.Landscape, 1280, 720, _fonts);

        var title = layout.Rows[0].Texts;
        Assert.Equal(2, title.Count);
        Assert.All(title, t => Assert.Equal(46, t.Size));
        Assert.EndsWith("…", title[1].Text, StringComparison.Ordinal);

        // A short title stays on one line, as large as fits: full size when there is room.
        var shortTitle = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts).Rows[0].Texts;
        Assert.Single(shortTitle);
        Assert.InRange(shortTitle[0].Size, 47, 58);
        var roomy = BumperLayout.Compute(BumperContent.For(Episode(series: "Lost")), ArtShape.Landscape, 1280, 720, _fonts).Rows[0].Texts;
        Assert.Equal(58, roomy[0].Size);
    }

    [Fact]
    public void WithoutArtwork_TheColumnTakesAReadableWidth()
    {
        var layout = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.None, 1280, 720, _fonts);
        Assert.Null(layout.Art);
        Assert.Equal(64, layout.Rows[0].Texts[0].Size);
    }

    [Fact]
    public void TheLayout_ScalesWithTheFrame()
    {
        var small = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts);
        var large = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1920, 1080, _fonts);

        Assert.Equal(1.5, large.Scale, 6);
        Assert.Equal(small.Art!.Value.Width * 1.5, large.Art!.Value.Width, 0);
        Assert.Equal(small.Rows[0].Texts[0].Size * 1.5, large.Rows[0].Texts[0].Size, 0);
        Assert.Equal(1920 - 108, large.CountdownNumber.X);
    }

    [Fact]
    public void TheChord_IsFourNotes_ThatSwellAndTrailOffToTheClipsEnd()
    {
        var chord = BumperArguments.ChordSource(15);
        Assert.StartsWith("aevalsrc=exprs='", chord, StringComparison.Ordinal);
        Assert.EndsWith(":s=48000:c=stereo:d=15", chord, StringComparison.Ordinal);
        foreach (var note in new[] { "146.83", "220", "293.66", "369.99" })
        {
            Assert.Contains("*" + note + "*t", chord, StringComparison.Ordinal);
        }

        // The card's version is cut to its own length.
        Assert.EndsWith("d=" + IntroService.IntroSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), BumperArguments.ChordSource(IntroService.IntroSeconds), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTuneInCard_PlaysTheChord()
    {
        var strips = new LineStrips(Path.Combine(_dir, "card"), 1280, 720);
        var args = string.Join(" ", IntroService.BuildSourceArguments(null, strips, "/tmp/out.mp4", null));
        Assert.Contains("aevalsrc=", args, StringComparison.Ordinal);
        Assert.Contains(BumperArguments.ChordFilter + "[a]", args, StringComparison.Ordinal);
        Assert.DoesNotContain("anullsrc", args, StringComparison.Ordinal);
    }

    [Fact]
    public void TheItemPass_ReadsEveryPieceOfTextFromAFile()
    {
        var layout = BumperLayout.Compute(BumperContent.For(Episode(series: "It's: a \"title\", [with] 100% risk")), ArtShape.Landscape, 1280, 720, _fonts);
        var written = new System.Collections.Generic.List<string>();
        var args = BumperArguments.BuildItemArguments(layout, "/tmp/layer.mp4", ["-f", "image2", "-loop", "1", "-i", "/tmp/art.jpg"], BumperShapeFiles.Under("/tmp/r"), text =>
        {
            written.Add(text);
            return "/tmp/t" + written.Count + ".txt";
        }, "/tmp/out.mp4");
        var filter = args[args.IndexOf("-filter_complex") + 1];

        // Titles go to drawtext as files, never inline, so no title needs escaping.
        Assert.DoesNotContain("100%", filter, StringComparison.Ordinal);
        Assert.Contains(written, w => w.Contains("100% risk", StringComparison.Ordinal));
        Assert.Contains("%{eif:max(1,ceil(15-t)):d}", written);
        Assert.Contains(":expansion=normal", filter, StringComparison.Ordinal);
        Assert.Contains("alphamerge", filter, StringComparison.Ordinal);
        Assert.Contains("fade=t=out:st=14.4:d=0.6", filter, StringComparison.Ordinal);
        Assert.Equal("/tmp/out.mp4", args[^1]);
        Assert.Contains("0:a", args);
    }

    [Fact]
    public void TheShapePass_DrawsOnlyWhatTheCardHas()
    {
        var files = BumperShapeFiles.Under("/tmp/s");
        var full = BumperArguments.BuildShapeArguments(BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts), files)!;
        Assert.Contains(files.ArtMask, full);
        Assert.Contains(files.Pill, full);
        Assert.Contains(files.BadgeRing, full);

        var bare = new ProgramEntry(Guid.NewGuid(), "Clip", null, TimeSpan.FromMinutes(3).Ticks, "/c.mp4") { ItemKind = "Video", RawName = "Clip" };
        Assert.Null(BumperArguments.BuildShapeArguments(BumperLayout.Compute(BumperContent.For(bare), ArtShape.None, 1280, 720, _fonts), files));
    }

    [Fact]
    public void ArtworkIsChosen_ThumbnailThenBackdropThenPoster()
    {
        var thumb = Touch("thumb.jpg");
        var backdrop = Touch("backdrop.jpg");
        var poster = Touch("poster.jpg");
        Assert.Equal(thumb, BumperService.ChooseArt(Episode(thumb: thumb, backdrop: backdrop, poster: poster)));
        Assert.Equal(backdrop, BumperService.ChooseArt(Episode(thumb: "/missing.jpg", backdrop: backdrop, poster: poster)));
        Assert.Equal(poster, BumperService.ChooseArt(Episode(poster: poster)));
        Assert.Null(BumperService.ChooseArt(Episode()));
    }

    [Fact]
    public void AMovie_ShowsItsPoster_FallingBackToItsLandscapeArt()
    {
        var thumb = Touch("movie-thumb.jpg");
        var backdrop = Touch("movie-backdrop.jpg");
        var poster = Touch("movie-poster.jpg");
        ProgramEntry Movie(string? p, string? t, string? b) => new(Guid.NewGuid(), "Film", null, TimeSpan.FromMinutes(100).Ticks, "/m.mkv")
        {
            ItemKind = "Movie",
            IsMovie = true,
            PrimaryImagePath = p,
            ThumbImagePath = t,
            BackdropImagePath = b,
        };

        Assert.Equal(poster, BumperService.ChooseArt(Movie(poster, thumb, backdrop)));
        Assert.Equal(thumb, BumperService.ChooseArt(Movie(null, thumb, backdrop)));
        Assert.Equal(backdrop, BumperService.ChooseArt(Movie("/missing.jpg", null, backdrop)));

        // A music video or home video is landscape first, like an episode.
        var clip = new ProgramEntry(Guid.NewGuid(), "Clip", null, TimeSpan.FromMinutes(4).Ticks, "/c.mp4") { ItemKind = "MusicVideo", PrimaryImagePath = poster, ThumbImagePath = thumb };
        Assert.Equal(thumb, BumperService.ChooseArt(clip));
    }

    [Fact]
    public void AClipEntry_PlaysLikeAnUnprobedLibraryItemWithNoLibraryId()
    {
        var entry = BumperService.ClipEntry("/tmp/b.mp4", "Up Next", 1920, 15);
        Assert.Equal(Guid.Empty, entry.ItemId);
        Assert.Equal(1080, entry.SourceHeight);
        Assert.Equal("bt709", entry.SourceColorSpace);
        Assert.Equal(TimeSpan.FromSeconds(15).Ticks, entry.DurationTicks);
        Assert.Empty(entry.Subtitles);
    }

    [Fact]
    public void TheThenLine_NamesAnEpisodeBySeriesAndNumber_AndAnythingElseByName()
    {
        Assert.Equal("The Long Way Home · S2 · E5", BumperContent.ThenText(Episode()));
        var special = new ProgramEntry(Guid.NewGuid(), "x", null, 1, "/x.mkv") { ItemKind = "Episode", SeriesName = "Show", RawName = "Making Of", SeasonNumber = 0 };
        Assert.Equal("Show · Special", BumperContent.ThenText(special));
        var movie = new ProgramEntry(Guid.NewGuid(), "Northbound", null, 1, "/m.mkv") { ItemKind = "Movie", IsMovie = true, RawName = "Northbound" };
        Assert.Equal("Northbound", BumperContent.ThenText(movie));
    }

    [Fact]
    public void TheThenLine_ShowsTheSlotStartOnTheServersClock()
    {
        var movie = new ProgramEntry(Guid.NewGuid(), "Northbound", null, 1, "/m.mkv") { ItemKind = "Movie", RawName = "Northbound" };
        Assert.Equal(new BumperThen("Northbound", "9:45 PM"), BumperThen.For(movie, new DateTime(2026, 10, 27, 21, 45, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
        Assert.Equal("10:05 AM", BumperThen.For(movie, new DateTime(2026, 10, 27, 10, 5, 0, DateTimeKind.Utc), TimeZoneInfo.Utc).Time);
    }

    [Fact]
    public void UnderWideArtwork_TheThenLineSpansTheFrame_WithTheTimeAtItsRightEdge()
    {
        var then = new BumperThen("Northbound", "9:45 PM");
        var plain = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts);
        var layout = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts, then);

        var art = layout.Art!.Value;
        Assert.True(art.Y < plain.Art!.Value.Y); // the artwork moves up to make room, centred with the line
        var row = layout.Rows[^1];
        Assert.False(row.Slides);
        Assert.Null(row.Divider);
        Assert.Equal(2.2, row.Delay);
        Assert.Equal(["THEN", "9:45 PM", "Northbound"], row.Texts.Select(t => t.Text));
        Assert.Equal(art.X, row.Texts[0].X);
        Assert.All(row.Texts, t => Assert.True(t.Baseline > art.Y + art.Height));
        var time = row.Texts[1];
        Assert.InRange(time.X + _fonts.Metrics(BumperFace.Regular).Measure("9:45 PM", time.Size), art.X + art.Width - 1, art.X + art.Width + 1);
    }

    [Fact]
    public void BesideAPoster_TheThenLineClosesTheInfoColumn_BelowAHairline()
    {
        var then = new BumperThen("The Long Way Home · S2 · E6", "10:30 PM");
        var layout = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Portrait, 1280, 720, _fonts, then);

        var row = layout.Rows[^1];
        Assert.False(row.Slides);
        var divider = row.Divider!.Value;
        Assert.Equal(72, divider.X);
        Assert.True(divider.Y > layout.Rows[^2].Texts[0].Baseline);
        Assert.True(row.Texts[0].Baseline > divider.Y);
        Assert.Equal(["THEN", "10:30 PM", "The Long Way Home · S2 · E6"], row.Texts.Select(t => t.Text));

        // The time follows THEN, so the name has the rest of the column.
        Assert.True(row.Texts[1].X < row.Texts[2].X);
        Assert.True(row.Texts[2].X < layout.Art!.Value.X);
    }

    [Fact]
    public void ALongName_IsCutOffWhereItsRoomEnds()
    {
        var then = new BumperThen("Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", "11:52 PM");
        var under = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Landscape, 1280, 720, _fonts, then).Rows[^1].Texts[2];
        var column = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Portrait, 1280, 720, _fonts, then).Rows[^1].Texts[2];

        Assert.EndsWith("…", under.Text, StringComparison.Ordinal);
        Assert.True(column.Text.Length > under.Text.Length); // the column has more room than the frame
    }

    [Fact]
    public void WithoutAThenLine_TheLayoutIsUnchanged()
    {
        var plain = BumperLayout.Compute(BumperContent.For(Episode()), ArtShape.Portrait, 1280, 720, _fonts);
        Assert.All(plain.Rows, r => Assert.True(r.Slides));
        Assert.Equal(4, plain.Rows.Count);
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static ProgramEntry Episode(string series = "The Long Way Home", string? seriesRating = "TV-14", string? ownRating = "TV-PG", string? thumb = null, string? backdrop = null, string? poster = null)
        => new(Guid.NewGuid(), series + " - Harbor Lights", null, TimeSpan.FromMinutes(44).Ticks, "/e.mkv")
        {
            ItemKind = "Episode",
            SeriesId = Guid.NewGuid(),
            SeriesName = series,
            RawName = "Harbor Lights",
            SeasonNumber = 2,
            EpisodeNumber = 5,
            Year = 2019,
            Genres = ["Drama"],
            CommunityRating = 8.4f,
            SeriesOfficialRating = seriesRating,
            OfficialRating = ownRating,
            ThumbImagePath = thumb,
            BackdropImagePath = backdrop,
            PrimaryImagePath = poster,
        };
}
