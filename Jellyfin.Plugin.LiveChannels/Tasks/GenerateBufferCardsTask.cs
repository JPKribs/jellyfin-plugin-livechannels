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
/// A scheduled task (Dashboard &gt; Scheduled Tasks, Live TV category) that renders the buffer cards of every enabled
/// channel that does not have current ones in the cache: the tune-in card in full, and the layer every Up Next card
/// of the channel shares, so the bumper render at an item boundary only adds the item's text, artwork and ratings,
/// and the scene of each placeholder block, whose message is drawn on while it plays.
/// It works from the plugin's configuration, not the guide, so cards and guide are built independently; it runs at
/// start-up, after Reset schedule, and on demand. A tune-in never renders a card itself: it plays the cached one, or
/// opens straight on content.
/// </summary>
public sealed class GenerateBufferCardsTask : PluginScheduledTask
{
    /// <summary>The task's key, which Reset schedule starts it by.</summary>
    public const string TaskKey = "LiveChannelsGenerateBufferCards";

    private readonly LiveChannelsTvService _service;
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateBufferCardsTask"/> class.
    /// </summary>
    /// <param name="service">The Live TV service, which knows the channels and their logos.</param>
    /// <param name="config">The server configuration, whose display language the name and description use.</param>
    public GenerateBufferCardsTask(LiveChannelsTvService service, IServerConfigurationManager config)
    {
        _service = service;
        _config = config;
    }

    /// <inheritdoc />
    public override string Name => BaseTranslations.Get(TranslationKeys.TaskGenerateBufferCards, BaseTranslations.ServerLanguage(_config));

    /// <inheritdoc />
    public override string Key => TaskKey;

    /// <inheritdoc />
    public override string Description => BaseTranslations.Get(TranslationKeys.TaskGenerateBufferCardsDescription, BaseTranslations.ServerLanguage(_config));

    /// <inheritdoc />
    public override string Category => "Live TV";

    /// <inheritdoc />
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.GenerateBufferCardsAsync(progress, cancellationToken);

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { OnStartup() };
}
