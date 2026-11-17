using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Jellyfin keeps every live stream it opens until it is told to close it. A viewer the plugin releases itself
/// (the watchdog, a teardown) is closed in Jellyfin too, and a viewer Jellyfin closed is not closed twice.
/// </summary>
[Collection(ScheduleCacheCollection.Name)]
public sealed class AbandonedStreamCloseTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "lc-abandoned-" + Guid.NewGuid().ToString("N"));
    private readonly IMediaSourceManager _mediaSources = Substitute.For<IMediaSourceManager>();

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    /// <summary>
    /// A released viewer is closed in Jellyfin under the id Jellyfin gave its stream.
    /// True: Jellyfin drops the stream it held for the viewer.
    /// False: the stream stays in Jellyfin's memory for as long as the server runs.
    /// </summary>
    [Fact]
    public async Task CloseInJellyfinAsync_ReleasedViewer_ClosesTheStreamJellyfinHolds()
    {
        using var service = Create();
        var stream = Stream("lc_viewer");

        // Jellyfin renames the opened source's live stream id in place once the open returns.
        service.Hand("lc_viewer", stream);
        stream.MediaSource.LiveStreamId = "prefix_lc_viewer";

        await service.CloseInJellyfinAsync("lc_viewer");

        await _mediaSources.Received(1).CloseLiveStream("prefix_lc_viewer");
    }

    /// <summary>
    /// A viewer is closed in Jellyfin once, however many rules release it.
    /// True: a second release sends nothing.
    /// False: a later viewer's stream under a reused id could be closed by a stale release.
    /// </summary>
    [Fact]
    public async Task CloseInJellyfinAsync_SecondRelease_SendsNothing()
    {
        using var service = Create();
        service.Hand("lc_viewer", Stream("lc_viewer"));

        await service.CloseInJellyfinAsync("lc_viewer");
        await service.CloseInJellyfinAsync("lc_viewer");

        await _mediaSources.Received(1).CloseLiveStream(Arg.Any<string>());
    }

    /// <summary>
    /// A viewer Jellyfin closed itself is not closed there again.
    /// True: the client close forgets the stream and nothing is sent back.
    /// False: every normal close would call back into Jellyfin while it holds its live stream lock.
    /// </summary>
    [Fact]
    public async Task CloseLiveStream_FromJellyfin_SendsNothingBack()
    {
        using var service = Create();
        service.Hand("lc_viewer", Stream("lc_viewer"));

        await service.CloseLiveStream("lc_viewer", CancellationToken.None);
        await service.CloseInJellyfinAsync("lc_viewer");

        await _mediaSources.DidNotReceive().CloseLiveStream(Arg.Any<string>());
    }

    /// <summary>
    /// A failed close in Jellyfin never reaches the watchdog that asked for it.
    /// True: the failure is swallowed.
    /// False: an exception on the reaper's timer thread would end the server process.
    /// </summary>
    [Fact]
    public async Task CloseInJellyfinAsync_JellyfinThrows_DoesNotThrow()
    {
        using var service = Create();
        _mediaSources.CloseLiveStream(Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("closed")));
        service.Hand("lc_viewer", Stream("lc_viewer"));

        await service.CloseInJellyfinAsync("lc_viewer");
    }

    private static DirectLiveStream Stream(string id)
        => new(
            "Channel",
            Path.GetTempPath(),
            DateTime.UtcNow,
            _ => new MediaSourceInfo { Id = id, LiveStreamId = id },
            () => Task.CompletedTask,
            NullLogger.Instance);

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
        var activity = new LiveChannelsActivity(new ActivityLogger(Substitute.For<IActivityManager>(), NullLogger<ActivityLogger>.Instance));
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
            _mediaSources,
            Substitute.For<IServerApplicationHost>(),
            paths,
            NullLogger<LiveChannelsTvService>.Instance);
    }
}
