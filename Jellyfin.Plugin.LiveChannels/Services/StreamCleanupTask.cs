using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// A scheduled task (Dashboard &gt; Scheduled Tasks, Live TV category) that clears leftover Live Channels stream
/// directories (each holds a channel's playlist and segments). Sessions are already cleaned on close and by a
/// periodic in-process reaper; this is the belt-and-braces safety net for one that got stuck or was never
/// released, and gives a manual "run now" button. It only ever deletes directories with no active session, so it
/// cannot interrupt a channel that is playing.
/// </summary>
public sealed class StreamCleanupTask : IScheduledTask
{
    private readonly LiveChannelsTvService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamCleanupTask"/> class.
    /// </summary>
    /// <param name="service">The Live TV service that owns the stream files and knows which are still active.</param>
    public StreamCleanupTask(LiveChannelsTvService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Clean Channels Directory";

    /// <inheritdoc />
    public string Key => "LiveChannelsStreamCleanup";

    /// <inheritdoc />
    public string Description => "Removes leftover Live Channels stream directories that are no longer tied to an active channel, in case one got stuck or was not cleared on close.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _service.CleanupOrphanStreams();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
}
