using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for placeholder blocks on the schedule: the window airs as one placeholder slot, content running into it
/// is cut where it opens, the loop carries on after it where it left off, the guide shows only the block's name,
/// and the stream loops the card through the window.
/// </summary>
public sealed class PlaceholderScheduleTests : IDisposable
{
    private static readonly DateTime Midnight = new(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly VideoEncoderProfile X264 = new("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);
    private readonly string _dir;

    public PlaceholderScheduleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lc-placeholder-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void AWindowWrappingPastMidnight_CoversBothSides()
    {
        var windows = new PlaceholderWindows([Placeholder(22 * 60, 2 * 60)], TimeZoneInfo.Utc);

        Assert.True(windows.TryFindAt(Midnight.AddHours(1), out var end, out _));
        Assert.Equal(Midnight.AddHours(2), end);
        Assert.True(windows.TryFindAt(Midnight.AddHours(23), out end, out _));
        Assert.Equal(Midnight.AddHours(26), end);
        Assert.False(windows.TryFindAt(Midnight.AddHours(3), out _, out _));
        Assert.Equal(Midnight.AddHours(22), windows.NextStartAfter(Midnight.AddHours(3)));
    }

    [Fact]
    public void TheWindow_AirsAsOnePlaceholder_AndContentResumesWhereItLeftOff()
    {
        var loop = Enumerable.Range(0, 6).Select(i => Item("item" + i, Hour)).ToList();
        var windows = new PlaceholderWindows([Placeholder(150, 240, "Off air")], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(8), "ch", null, out _, exactStart: true, placeholders: windows);

        var cut = schedule.Single(s => s.Start == Midnight.AddHours(2));
        Assert.Equal(Midnight.AddMinutes(150), cut.Stop);
        Assert.False(cut.Program.IsPlaceholder);

        var placeholder = schedule.Single(s => s.Program.IsPlaceholder);
        Assert.Equal(Midnight.AddMinutes(150), placeholder.Start);
        Assert.Equal(Midnight.AddHours(4), placeholder.Stop);
        Assert.Equal("Off air", placeholder.Program.Title);
        Assert.Equal(TimeSpan.Zero, placeholder.Lead);

        var after = schedule.Single(s => s.Start == Midnight.AddHours(4));
        Assert.Same(loop[(loop.IndexOf(cut.Program) + 1) % loop.Count], after.Program);
        Assert.DoesNotContain(schedule, s => !s.Program.IsPlaceholder && s.Start < placeholder.Stop && s.Stop > placeholder.Start);
    }

    [Fact]
    public void AnItemThatWouldBarelyStart_LetsTheWindowOpenEarly()
    {
        var loop = new List<ProgramEntry> { Item("a", Hour - TimeSpan.FromSeconds(20)), Item("b", Hour), Item("c", Hour) };
        var windows = new PlaceholderWindows([Placeholder(60, 120)], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(3), "ch", null, out _, exactStart: true, placeholders: windows);

        Assert.True(schedule[1].Program.IsPlaceholder);
        Assert.Equal(Midnight.AddHours(1) - TimeSpan.FromSeconds(20), schedule[1].Start);
        Assert.Same(loop[1], schedule[2].Program);
        Assert.Equal(Midnight.AddHours(2), schedule[2].Start);
    }

    [Fact]
    public void AShortItemThatEndsBeforeTheWindow_StillAirs()
    {
        var clip = Item("clip", TimeSpan.FromSeconds(15));
        var loop = new List<ProgramEntry> { Item("a", Hour - TimeSpan.FromSeconds(25)), clip, Item("c", Hour) };
        var windows = new PlaceholderWindows([Placeholder(60, 120)], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(3), "ch", null, out _, exactStart: true, placeholders: windows);

        Assert.Same(clip, schedule[1].Program);
        Assert.Equal(TimeSpan.FromSeconds(15), schedule[1].Stop - schedule[1].Start);
        Assert.True(schedule[2].Program.IsPlaceholder);
        Assert.Equal(Midnight.AddHours(1) - TimeSpan.FromSeconds(10), schedule[2].Start);
    }

    [Fact]
    public void ACutLeavingUnderAMinuteOfTheItem_DropsItsBumper()
    {
        var lead = TimeSpan.FromSeconds(10);
        var loop = new ProgramLoop([Item("a", Hour - TimeSpan.FromSeconds(70)), Item("b", Hour), Item("c", Hour - TimeSpan.FromMinutes(5))], null, lead.Ticks);
        var windows = new PlaceholderWindows([Placeholder(60, 120)], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(3), "ch", null, out _, exactStart: true, placeholders: windows);

        var cut = schedule.Single(s => s.Program.Title == "b" && s.Stop == Midnight.AddHours(1));
        Assert.Equal(TimeSpan.FromSeconds(60), cut.Stop - cut.Start);
        Assert.Equal(TimeSpan.Zero, cut.Lead);
    }

    [Fact]
    public void RememberedWindowAnswers_MatchFreshOnes()
    {
        RatingBlock[] blocks = [Placeholder(22 * 60, 2 * 60), Placeholder(5 * 60, 6 * 60), Placeholder(12 * 60, 12 * 60 + 1)];
        var cached = new PlaceholderWindows(blocks, TimeZoneInfo.Utc);
        for (var t = Midnight.AddHours(-30); t < Midnight.AddHours(30); t = t.AddMinutes(7))
        {
            var fresh = new PlaceholderWindows(blocks, TimeZoneInfo.Utc);
            Assert.Equal(fresh.TryFindAt(t, out var freshEnd, out _), cached.TryFindAt(t, out var cachedEnd, out _));
            Assert.Equal(freshEnd, cachedEnd);
            Assert.Equal(fresh.NextStartAfter(t), cached.NextStartAfter(t));
        }
    }

    [Fact]
    public void AWindowRemovedWhileItAirs_RejoinsTheScheduleWherePlaybackReached()
    {
        var service = NewChannelService();
        var from = LocalMidnightUtc(Midnight);
        var channel = new Channel { Id = "c", Number = 4 };
        channel.RatingBlocks.Add(Placeholder(LocalMinute(from), LocalMinute(from.AddHours(6))));
        var loop = new ProgramLoop(Enumerable.Range(0, 3).Select(i => Item("item" + i, Hour)).ToList(), from);
        var schedule = new DaypartStreamSchedule(service, channel, loop, from);
        Assert.True(schedule.Current.Program.IsPlaceholder);

        var edited = new Channel { Id = "c", Number = 4 };
        schedule.RejoinAfter(TimeSpan.FromMinutes(90), edited);

        Assert.False(schedule.Current.Program.IsPlaceholder);
        Assert.Equal(TimeSpan.FromMinutes(30), schedule.Current.Offset);
    }

    [Fact]
    public void AnItemLeftOnlyItsBumper_DoesNotStart()
    {
        var lead = TimeSpan.FromSeconds(10);
        var loop = new ProgramLoop([Item("a", Hour - TimeSpan.FromSeconds(15)), Item("b", Hour)], null, lead.Ticks);
        var windows = new PlaceholderWindows([Placeholder(60, 120)], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(3), "ch", null, out _, exactStart: true, placeholders: windows);

        // Five seconds before the window is too little to start "b" at all, bumper or not: the window opens then.
        Assert.Equal(lead, schedule[0].Lead);
        Assert.True(schedule[1].Program.IsPlaceholder);
        Assert.Equal(Midnight.AddHours(1) - TimeSpan.FromSeconds(5), schedule[1].Start);
        Assert.Equal("b", schedule[2].Program.Title);
    }

    [Fact]
    public void ASplicedLoopAnchoredInsideTheWindow_StartsItsFirstItemWhenTheWindowEnds()
    {
        var loop = Enumerable.Range(0, 3).Select(i => Item("item" + i, Hour)).ToList();
        var windows = new PlaceholderWindows([Placeholder(120, 240)], TimeZoneInfo.Utc);
        var anchor = Midnight.AddHours(2);

        var schedule = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, anchor, anchor, anchor.AddHours(4), "ch", null, out _, exactStart: true, placeholders: windows);

        Assert.True(schedule[0].Program.IsPlaceholder);
        Assert.Same(loop[0], schedule[1].Program);
        Assert.Equal(Midnight.AddHours(4), schedule[1].Start);
    }

    [Fact]
    public void AResumedWalk_MatchesAFreshOne_AcrossAWindow()
    {
        var loop = Enumerable.Range(0, 5).Select(i => Item("item" + i, TimeSpan.FromMinutes(50))).ToList();
        var windows = new PlaceholderWindows([Placeholder(300, 420)], TimeZoneInfo.Utc);

        var whole = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(12), "ch", null, out _, placeholders: windows);
        var first = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(6), "ch", null, out var state, placeholders: windows);
        var second = DaypartSchedule.BuildResumable(loop, [], 0, TimeZoneInfo.Utc, Midnight, Midnight.AddHours(6), Midnight.AddHours(12), "ch", state, out _, placeholders: windows);

        var joined = first.Concat(second.Where(s => first.All(f => f.Start != s.Start))).ToList();
        Assert.Equal(whole.Select(s => (s.Program.Title, s.Start, s.Stop)), joined.Select(s => (s.Program.Title, s.Start, s.Stop)));
    }

    [Fact]
    public void PlaceholderBlocks_NeverLimitRatings_AndLegacyLimitsStillApply()
    {
        var channel = new Channel { MaxOfficialRating = "TV-14" };
        channel.RatingBlocks.Add(Placeholder(60, 120));
        channel.RatingBlocks.Add(new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.AllDay });

        var limits = channel.EffectiveRatingBlocks();
        Assert.Single(limits);
        Assert.Equal("TV-14", limits[0].MaxOfficialRating);
        Assert.Single(channel.PlaceholderBlocks());
    }

    [Fact]
    public void AChannelWithOnlyAPlaceholder_UsesTheTimeOfDaySchedule()
    {
        var service = NewChannelService();
        var channel = new Channel { Id = "c", Number = 4 };
        channel.RatingBlocks.Add(Placeholder(120, 180));
        var loop = Enumerable.Range(0, 4).Select(i => Item("item" + i, Hour)).ToList();
        var from = LocalMidnightUtc(Midnight);

        Assert.True(service.IsTimeOfDayChannel(channel));
        var placeholder = service.BuildTimeline(channel, loop, from, from.AddHours(6)).Single(s => s.Program.IsPlaceholder);
        Assert.Equal(Hour, placeholder.Stop - placeholder.Start);
    }

    [Fact]
    public void ARebuildDuringTheWindow_KeepsTheWindowAndWhatFollowsIt()
    {
        var service = NewChannelService();
        var channel = new Channel { Id = "c", Number = 4 };
        channel.RatingBlocks.Add(Placeholder(120, 240));
        var from = LocalMidnightUtc(Midnight);
        var previous = new ProgramLoop(Enumerable.Range(0, 5).Select(i => Item("item" + i, Hour)).ToList(), from);
        var timeline = service.BuildTimeline(channel, previous, from, from.AddHours(6));
        var window = timeline.Single(s => s.Program.IsPlaceholder);
        var due = timeline.First(s => s.Start == window.Stop).Program;

        var rebuilt = new ProgramLoop(previous.Reverse().ToList(), null);
        var spliced = service.Splice(channel, previous, rebuilt, window.Start.AddMinutes(30));

        Assert.Equal(window.Start, spliced.AnchorUtc);
        Assert.Same(due, spliced[0]);
        var after = service.BuildTimeline(channel, spliced, window.Start, window.Stop.AddHours(1));
        Assert.True(after[0].Program.IsPlaceholder);
        Assert.Equal(window.Stop, after[0].Stop);
        Assert.Same(due, after[1].Program);
    }

    [Fact]
    public void TheGuideEntry_ShowsOnlyTheName()
    {
        var slot = ScheduledProgram.ForPlaceholder(Placeholder(0, 60, "  "), Midnight, Midnight.AddHours(1));
        var info = LiveChannelsTvService.PlaceholderProgram(new Channel { Id = "c" }, "c", slot);

        Assert.Equal("-", info.Name);
        Assert.Equal(Midnight, info.StartDate);
        Assert.Equal(Midnight.AddHours(1), info.EndDate);
        Assert.Null(info.ProductionYear);
        Assert.Null(info.EpisodeNumber);
        Assert.Null(info.OfficialRating);
        Assert.False(info.IsMovie);
        Assert.False(info.HasImage);
        Assert.Equal(info.ShowId, LiveChannelsTvService.PlaceholderProgram(new Channel { Id = "c" }, "c", ScheduledProgram.ForPlaceholder(Placeholder(0, 60), Midnight.AddDays(1), Midnight.AddDays(1).AddHours(1))).ShowId);
    }

    [Fact]
    public void TheStream_CapsAnItemCutByAWindow()
    {
        var service = NewChannelService();
        var channel = new Channel { Id = "c", Number = 4 };
        var from = LocalMidnightUtc(Midnight);
        channel.RatingBlocks.Add(Placeholder(LocalMinute(from.AddMinutes(90)), LocalMinute(from.AddMinutes(150))));
        var loop = new ProgramLoop(Enumerable.Range(0, 3).Select(i => Item("item" + i, Hour)).ToList(), from);

        var schedule = new DaypartStreamSchedule(service, channel, loop, from.AddMinutes(70));
        Assert.Equal(TimeSpan.FromMinutes(10), schedule.Current.Offset);
        Assert.Equal(TimeSpan.FromMinutes(20), schedule.Current.DurationLimit);

        schedule.Advance();
        Assert.True(schedule.Current.Program.IsPlaceholder);
        Assert.Equal(TimeSpan.Zero, schedule.Current.Offset);
        Assert.Equal(TimeSpan.FromMinutes(60), TimeSpan.FromTicks(schedule.Current.Program.DurationTicks));
    }

    [Fact]
    public void TheCard_JoinsAtTheWindowsOffset_AndPlaysAsOneLoopingProducer()
    {
        var card = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(5), StreamSessionService.PlaceholderStart(TimeSpan.FromSeconds(25), card));
        Assert.Equal(TimeSpan.Zero, StreamSessionService.PlaceholderStart(TimeSpan.Zero, card));

        var args = StreamArguments.Build("/card.mp4", TimeSpan.FromSeconds(5), TimeSpan.Zero, 1280, 4000, X264, "aac", 192, null, softwareDecode: true, durationLimit: TimeSpan.FromHours(2), loopInput: true);
        var input = args.IndexOf("-i");
        Assert.Equal("-1", args[args.IndexOf("-stream_loop") + 1]);
        Assert.True(args.IndexOf("-stream_loop") < input && args.IndexOf("-ss") < input && args.IndexOf("-t") < input);
        Assert.Equal("7200.000", args[args.IndexOf("-t") + 1]);
        Assert.DoesNotContain("-stream_loop", StreamArguments.Build("/item.mkv", TimeSpan.Zero, TimeSpan.Zero, 1280, 4000, X264, "aac", 192, null));
    }

    [Fact]
    public void ABlockCutByAWindow_IsCheckedAgainWhenTheWindowEnds()
    {
        const int Pg = 100;
        const int R = 400;
        ResolvedRatingBlock[] blocks =
        [
            new(new RatingWindow(null, Pg, true), AllDay: false, 6 * 60, 20 * 60),
            new(new RatingWindow(null, R, true), AllDay: false, 20 * 60, 6 * 60),
        ];
        var series = Guid.NewGuid();
        var loop = new List<ProgramEntry>
        {
            Rated(Item("movie", Hour), Pg),
            Rated(Episode(series, 1), R),
            Rated(Episode(series, 2), R),
            Rated(Episode(series, 3), R),
            Rated(Item("movie2", Hour), Pg),
            Rated(Item("movie3", Hour), Pg),
        };
        var windows = new PlaceholderWindows([Placeholder(120, 360)], TimeZoneInfo.Utc);

        var schedule = DaypartSchedule.BuildResumable(loop, blocks, 0, TimeZoneInfo.Utc, Midnight, Midnight, Midnight.AddHours(12), "ch", null, out _, exactStart: true, placeholders: windows);

        Assert.Equal(1, schedule.Single(s => s.Start == Midnight.AddHours(1)).Program.EpisodeNumber);
        Assert.DoesNotContain(schedule, s => s.Start >= Midnight.AddHours(6) && s.Program.ParentalRatingValue == R);
    }

    [Fact]
    public void TheMessage_NamesWhatFollowsAWindowPastItsNeighbours()
    {
        var after = Item("Heat", Hour);
        var schedule = new FixedSchedule(
            ScheduledProgram.ForPlaceholder(Placeholder(0, 60), Midnight, Midnight.AddHours(1)),
            ScheduledProgram.ForPlaceholder(Placeholder(60, 120), Midnight.AddHours(1), Midnight.AddHours(2)),
            new ScheduledProgram(after, Midnight.AddHours(2), Midnight.AddHours(3)));

        Assert.Equal((after, Midnight.AddHours(2)), StreamSessionService.ContentAfter(schedule));
    }

    [Fact]
    public void TheStream_SeesPastItsHorizon_ForWhatFollowsALongWindow()
    {
        var service = NewChannelService();
        var from = LocalMidnightUtc(Midnight);
        var channel = new Channel { Id = "c", Number = 4 };
        channel.RatingBlocks.Add(Placeholder(LocalMinute(from), LocalMinute(from.AddHours(6))));
        var loop = new ProgramLoop(Enumerable.Range(0, 3).Select(i => Item("item" + i, Hour)).ToList(), from);

        var schedule = new DaypartStreamSchedule(service, channel, loop, from);

        Assert.True(schedule.Current.Program.IsPlaceholder);
        Assert.Equal(from.AddHours(6), schedule.Following(1)?.StartUtc);
        Assert.Same(loop[0], schedule.Following(1)?.Program);
    }

    [Fact]
    public void ARebuildDuringBackToBackWindows_StillKeepsWhatFollowsThem()
    {
        var service = NewChannelService();
        var channel = new Channel { Id = "c", Number = 4 };
        channel.RatingBlocks.Add(Placeholder(120, 240));
        channel.RatingBlocks.Add(Placeholder(240, 300));
        var from = LocalMidnightUtc(Midnight);
        var previous = new ProgramLoop(Enumerable.Range(0, 5).Select(i => Item("item" + i, Hour)).ToList(), from);
        var timeline = service.BuildTimeline(channel, previous, from, from.AddHours(8));
        var first = timeline.First(s => s.Program.IsPlaceholder);
        var due = timeline.First(s => !s.Program.IsPlaceholder && s.Start >= first.Stop).Program;

        var spliced = service.Splice(channel, previous, new ProgramLoop(previous.Reverse().ToList(), null), first.Start.AddMinutes(30));

        Assert.Equal(first.Start, spliced.AnchorUtc);
        Assert.Same(due, spliced[0]);
    }

    [Fact]
    public void EditingOnlyTheCard_KeepsTheSchedules()
    {
        static PluginConfiguration Config(Action<RatingBlock> change)
        {
            var block = Placeholder(60, 120);
            change(block);
            return With(block);
        }

        var stored = Config(_ => { });
        Assert.False(Plugin.AffectsSchedule(Config(b => { b.PlaceholderTitle = "Back soon"; b.PlaceholderStyle = PlaceholderStyle.Rain; b.PlaceholderName = "Off air"; }), stored));
        Assert.True(Plugin.AffectsSchedule(Config(b => b.EndMinutes = 180), stored));
    }

    [Fact]
    public void AnUpNextCardBeforeAWindow_HasNoThenLine()
    {
        var slot = ScheduledProgram.ForPlaceholder(Placeholder(0, 60), Midnight, Midnight.AddHours(1));
        Assert.Null(StreamSessionService.ThenFor((slot.Program, slot.Start)));
    }

    [Fact]
    public void TheMessage_NamesAnEpisodeBySeries()
    {
        var episode = new ProgramEntry(Guid.NewGuid(), "The Office - Pilot", null, Hour.Ticks, "/e.mkv") { SeriesId = Guid.NewGuid(), SeriesName = "The Office" };
        Assert.Equal("The Office", episode.GuideName);
        Assert.Equal("Heat", Item("Heat", Hour).GuideName);
    }

    [Fact]
    public void Validation_RequiresAWindow_ForbidsOverlap_AndNeedsACustomPath_ButNotThatItIsThere()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(With(new RatingBlock { IsPlaceholder = true, Period = RatingBlockPeriod.AllDay })));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(With(Placeholder(60, 180), Placeholder(120, 240))));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(With(Placeholder(22 * 60, 2 * 60), Placeholder(60, 90))));
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(With(Placeholder(22 * 60, 2 * 60), Placeholder(23 * 60, 23 * 60 + 30))));
        ConfigurationValidator.Validate(With(Placeholder(22 * 60, 2 * 60), Placeholder(2 * 60, 4 * 60), Placeholder(20 * 60, 22 * 60)));
        var custom = Placeholder(60, 120);
        custom.PlaceholderStyle = PlaceholderStyle.Custom;
        Assert.Throws<ArgumentException>(() => ConfigurationValidator.Validate(With(custom)));
        custom.PlaceholderMediaPath = Path.Combine(_dir, "offline-share.mp4");
        ConfigurationValidator.Validate(With(custom));

        var named = Placeholder(60, 120, "   ");
        ConfigurationValidator.Validate(With(named, Placeholder(120, 180)));
        Assert.Equal("-", named.PlaceholderName);
    }

    private static PluginConfiguration With(params RatingBlock[] blocks)
    {
        var channel = new Channel { Id = "id1", Name = "Ch1", Number = 1, Sources = { new LibrarySource { LibraryId = "lib" } } };
        channel.RatingBlocks.AddRange(blocks);
        var config = new PluginConfiguration();
        config.Channels.Add(channel);
        return config;
    }

    private static RatingBlock Placeholder(int start, int end, string name = "-")
        => new() { IsPlaceholder = true, Period = RatingBlockPeriod.Custom, StartMinutes = start, EndMinutes = end, PlaceholderName = name };

    private static ProgramEntry Rated(ProgramEntry entry, int rating)
        => new(entry.ItemId, entry.Title, null, entry.DurationTicks, entry.Path)
        {
            SeriesId = entry.SeriesId,
            SeriesName = entry.SeriesName,
            EpisodeNumber = entry.EpisodeNumber,
            SeasonNumber = entry.SeasonNumber,
            RawName = entry.RawName,
            ParentalRatingValue = rating,
        };

    private static ProgramEntry Episode(Guid series, int number)
        => new(Guid.NewGuid(), "Show " + number, null, Hour.Ticks, "/e.mkv") { SeriesId = series, SeriesName = "Show", SeasonNumber = 1, EpisodeNumber = number, RawName = "e" + number };

    private static ProgramEntry Item(string title, TimeSpan length)
        => new(Guid.NewGuid(), title, null, length.Ticks, "/media/" + title + ".mkv");

    private static DateTime LocalMidnightUtc(DateTime day)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private static int LocalMinute(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        return (local.Hour * 60) + local.Minute;
    }

    private ChannelService NewChannelService()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(Path.Combine(_dir, "cache"));
        paths.DataPath.Returns(Path.Combine(_dir, "data"));
        return new ChannelService(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            NullLogger<ChannelService>.Instance);
    }

    private sealed class FixedSchedule : IStreamSchedule
    {
        private readonly ScheduledProgram[] _slots;

        public FixedSchedule(params ScheduledProgram[] slots) => _slots = slots;

        public int PoolCount => _slots.Length;

        public StreamStep Current => new(_slots[0].Program, TimeSpan.Zero, null, TimeSpan.Zero);

        public (ProgramEntry Program, DateTime StartUtc)? Following(int ahead)
            => ahead < _slots.Length ? (_slots[ahead].Program, _slots[ahead].Start) : null;

        public void Advance()
        {
        }
    }
}
