using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// A scheduled task (Dashboard &gt; Scheduled Tasks, Live TV category) that renders the buffer cards of every enabled
/// channel that does not have current ones in the cache: the tune-in card in full, and the layer every Up Next card
/// of the channel shares, so the bumper render at an item boundary only adds the item's text, artwork and ratings.
/// It works from the plugin's configuration, not the guide, so cards and guide are built independently; it runs at
/// start-up, after Reset schedule, and on demand. A tune-in never renders a card itself: it plays the cached one, or
/// opens straight on content.
/// </summary>
public sealed class GenerateBufferCardsTask : PluginScheduledTask
{
    /// <summary>The task's key, which Reset schedule starts it by.</summary>
    public const string TaskKey = "LiveChannelsGenerateBufferCards";

    private readonly LiveChannelsTvService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateBufferCardsTask"/> class.
    /// </summary>
    /// <param name="service">The Live TV service, which knows the channels and their logos.</param>
    public GenerateBufferCardsTask(LiveChannelsTvService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public override string Name => "Generate Buffer Cards";

    /// <inheritdoc />
    public override string Key => TaskKey;

    /// <inheritdoc />
    public override string Description => "Renders the tune-in card and the shared Up Next layer of every enabled Live Channels channel that does not have current ones, so tuning in never waits on a render and an Up Next card only adds the item.";

    /// <inheritdoc />
    public override string Category => "Live TV";

    /// <inheritdoc />
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.GenerateBufferCardsAsync(progress, cancellationToken);

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { OnStartup() };
}
