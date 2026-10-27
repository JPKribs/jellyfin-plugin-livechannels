using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The stale-guide purge removes what a refresh no longer publishes in one batch: most of a channel's stored guide
/// goes stale on every rebuild, and a delete per programme held Jellyfin's database lock for minutes.
/// </summary>
public class GuidePurgeTests
{
    [Fact]
    public void StalePrograms_AreDeletedInOneBatch_AndCurrentOnesKept()
    {
        var library = Substitute.For<ILibraryManager>();
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "chan", ServiceName = "Live Channels", Name = "Chan" };
        var programs = Enumerable.Range(0, 5).Select(i => new LiveTvProgram { Id = Guid.NewGuid(), ExternalId = "chan_" + i }).ToList();
        var call = 0;
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => call++ == 0 ? new List<BaseItem> { channel } : programs.Cast<BaseItem>().ToList());

        IReadOnlyCollection<BaseItem>? deleted = null;
        library.When(l => l.DeleteItemsUnsafeFast(Arg.Any<IReadOnlyCollection<BaseItem>>(), Arg.Any<bool>()))
            .Do(c => deleted = c.Arg<IReadOnlyCollection<BaseItem>>());

        Create(library).PurgeStaleGuidePrograms("Live Channels", "chan", new HashSet<string>(StringComparer.Ordinal) { "chan_1", "chan_3" });

        Assert.NotNull(deleted);
        Assert.Equal(new[] { "chan_0", "chan_2", "chan_4" }, deleted!.Cast<LiveTvProgram>().Select(p => p.ExternalId).OrderBy(x => x, StringComparer.Ordinal));
        library.Received(1).DeleteItemsUnsafeFast(Arg.Any<IReadOnlyCollection<BaseItem>>(), false);
        library.DidNotReceive().DeleteItem(Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
    }

    [Fact]
    public void NothingStale_DeletesNothing()
    {
        var library = Substitute.For<ILibraryManager>();
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "chan", ServiceName = "Live Channels", Name = "Chan" };
        var call = 0;
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => call++ == 0
            ? new List<BaseItem> { channel }
            : new List<BaseItem> { new LiveTvProgram { Id = Guid.NewGuid(), ExternalId = "chan_0" } });

        Create(library).PurgeStaleGuidePrograms("Live Channels", "chan", new HashSet<string>(StringComparer.Ordinal) { "chan_0" });

        library.DidNotReceive().DeleteItemsUnsafeFast(Arg.Any<IReadOnlyCollection<BaseItem>>(), Arg.Any<bool>());
    }

    private static ChannelService Create(ILibraryManager library)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(System.IO.Path.GetTempPath());
        return new ChannelService(
            library,
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            NullLogger<ChannelService>.Instance);
    }
}
