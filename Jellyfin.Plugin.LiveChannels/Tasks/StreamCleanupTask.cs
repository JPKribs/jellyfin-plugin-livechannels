using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Tasks;

/// <summary>
/// A scheduled task (Dashboard &gt; Scheduled Tasks, Live TV category) that clears leftover Live Channels stream
/// directories (each holds a channel's playlist and segments). Sessions are already cleaned on close and by a
/// periodic in-process reaper; this is the belt-and-braces safety net for one that got stuck or was never
/// released, and gives a manual "run now" button. It only ever deletes directories with no active session, so it
/// cannot interrupt a channel that is playing.
/// </summary>
public sealed class StreamCleanupTask : PluginScheduledTask
{
    private readonly LiveChannelsTvService _service;
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamCleanupTask"/> class.
    /// </summary>
    /// <param name="service">The Live TV service that owns the stream files and knows which are still active.</param>
    /// <param name="config">The server configuration, whose display language the name and description use.</param>
    public StreamCleanupTask(LiveChannelsTvService service, IServerConfigurationManager config)
    {
        _service = service;
        _config = config;
    }

    /// <inheritdoc />
    public override string Name => BaseTranslations.Get(TranslationKeys.TaskCleanChannelsDirectory, BaseTranslations.ServerLanguage(_config));

    /// <inheritdoc />
    public override string Key => "LiveChannelsStreamCleanup";

    /// <inheritdoc />
    public override string Description => BaseTranslations.Get(TranslationKeys.TaskCleanChannelsDirectoryDescription, BaseTranslations.ServerLanguage(_config));

    /// <inheritdoc />
    public override string Category => "Live TV";

    /// <inheritdoc />
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _service.CleanupOrphanStreams();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { EveryInterval(TimeSpan.FromHours(12)) };
}
