using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Writes the plugin's events to Jellyfin's activity log, where administrators already look. Entries are fire
/// and forget and failures are swallowed, since an activity entry must never break the flow it documents.
/// </summary>
public sealed class ActivityLogger
{
    private readonly IActivityManager _activityManager;
    private readonly ILogger<ActivityLogger> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityLogger"/> class.
    /// </summary>
    /// <param name="activityManager">Jellyfin's activity manager.</param>
    /// <param name="logger">The logger.</param>
    public ActivityLogger(IActivityManager activityManager, ILogger<ActivityLogger> logger)
    {
        _activityManager = activityManager;
        _logger = logger;
    }

    /// <summary>
    /// Writes an entry to the activity log without awaiting or throwing.
    /// </summary>
    /// <param name="name">The entry headline shown in the activity feed.</param>
    /// <param name="type">The entry type, namespaced under <c>LiveChannels.</c>.</param>
    /// <param name="overview">Optional detail text.</param>
    /// <param name="severity">The severity, informational by default.</param>
    public void Log(string name, string type, string? overview = null, LogLevel severity = LogLevel.Information)
        => _ = WriteAsync(name, type, overview, severity);

    private async Task WriteAsync(string name, string type, string? overview, LogLevel severity)
    {
        try
        {
            await _activityManager.CreateAsync(new ActivityLog(name, type, Guid.Empty)
            {
                ShortOverview = overview ?? string.Empty,
                LogSeverity = severity
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write activity log entry of type {Type}", type);
        }
    }
}
