using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The "Clean Channels Directory" task and the start-up sweep remove only the plugin's own leftover session
/// directories: never a live session's, never the schedule cache, never anything else in a user-chosen root.
/// </summary>
public sealed class StreamCleanupTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "lc-cleanup-" + Guid.NewGuid().ToString("N"));

    private string StreamRoot => Path.Combine(_cache, "livechannels");

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void StartUp_RemovesLeftoverSessions_AndNothingElse()
    {
        var orphan = SessionDir();
        var schedule = Directory.CreateDirectory(Path.Combine(StreamRoot, ChannelService.ScheduleDirName)).FullName;
        var foreign = Directory.CreateDirectory(Path.Combine(StreamRoot, "my-files")).FullName;
        var lookalike = Directory.CreateDirectory(Path.Combine(StreamRoot, "lc_NOT-A-SESSION")).FullName;

        using var service = Create();

        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(schedule));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(lookalike));
    }

    [Fact]
    public void TheCleanupTask_RemovesOrphanedSessions_AndNothingElse()
    {
        using var service = Create();

        // Left behind after start-up (a session that got stuck), beside things that are not the plugin's.
        var orphanA = SessionDir();
        var orphanB = SessionDir();
        var foreign = Directory.CreateDirectory(Path.Combine(StreamRoot, "my-files")).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");

        Assert.Equal(2, service.CleanupOrphanStreams());
        Assert.False(Directory.Exists(orphanA));
        Assert.False(Directory.Exists(orphanB));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.Equal(0, service.CleanupOrphanStreams());
    }

    private string SessionDir()
    {
        var dir = Path.Combine(StreamRoot, "lc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "seg0.ts"), "ts");
        return dir;
    }

    private LiveChannelsTvService Create()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(_cache);
        paths.DataPath.Returns(Path.Combine(_cache, "data"));
        paths.LogDirectoryPath.Returns(Path.Combine(_cache, "logs"));

        var encoder = Substitute.For<IMediaEncoder>();
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(new EncodingOptions());
        var encoders = new EncoderResolver(config, NullLogger<EncoderResolver>.Instance);
        var library = Substitute.For<ILibraryManager>();

        var channels = new ChannelService(
            library,
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            NullLogger<ChannelService>.Instance);
        var intros = new IntroService(encoder, encoders, paths, NullLogger<IntroService>.Instance);
        var streams = new StreamSessionService(encoder, channels, encoders, intros, paths, NullLogger<StreamSessionService>.Instance);
        var activity = new ActivityLogger(Substitute.For<IActivityManager>(), NullLogger<ActivityLogger>.Instance);
        var timers = new TimerService(paths, NullLogger<TimerService>.Instance);
        var recordings = new RecordingService(channels, timers, library, Substitute.For<IProviderManager>(), config, Substitute.For<IFileSystem>(), paths, activity, NullLogger<RecordingService>.Instance);
        var stress = new StressTestService(encoder, streams, library, Substitute.For<IMediaSourceManager>(), NullLogger<StressTestService>.Instance);

        return new LiveChannelsTvService(
            channels,
            streams,
            new DefaultLogoService(encoder, paths, NullLogger<DefaultLogoService>.Instance),
            intros,
            activity,
            timers,
            recordings,
            stress,
            Substitute.For<ISessionManager>(),
            Substitute.For<IServerApplicationHost>(),
            paths,
            NullLogger<LiveChannelsTvService>.Instance);
    }
}
