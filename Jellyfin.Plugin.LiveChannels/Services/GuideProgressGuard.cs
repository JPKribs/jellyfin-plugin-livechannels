using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Keeps the Refresh Guide task's percentage from ever going backwards. Jellyfin reports each Live TV service,
/// and then each database cleanup pass, from zero on the same bar, so with this plugin installed (a second
/// service) the bar restarts several times in one run. This puts a filter in front of the guide manager the
/// task calls that drops every report lower than one already shown.
/// </summary>
public sealed class GuideProgressGuard : IHostedService
{
    private const string RefreshGuideKey = "RefreshGuide";

    private readonly ITaskManager _taskManager;
    private readonly ILogger<GuideProgressGuard> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideProgressGuard"/> class.
    /// </summary>
    /// <param name="taskManager">The task manager, used to find the Refresh Guide task.</param>
    /// <param name="logger">The logger.</param>
    public GuideProgressGuard(ITaskManager taskManager, ILogger<GuideProgressGuard> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Never throws: the guard is cosmetic, and a Jellyfin whose task is shaped differently must still start
        // and refresh its guide exactly as it would without the plugin.
        try
        {
            Install();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not steady the Refresh Guide progress; the guide refresh itself is unaffected");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The task holds its guide manager in a private field and Jellyfin registers its own guide manager after
    // plugin services, so there is no registration to override: the field is the only place to step in. It is
    // found by type, not name, so a rename upstream does not break it.
    private void Install()
    {
        var task = _taskManager.ScheduledTasks
            .Select(worker => worker.ScheduledTask)
            .FirstOrDefault(t => string.Equals(t.Key, RefreshGuideKey, StringComparison.Ordinal));
        var field = task?.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(IGuideManager));

        if (task is null || field is null || field.GetValue(task) is not IGuideManager inner)
        {
            _logger.LogInformation("Live Channels: the Refresh Guide task was not found in its expected shape; its progress is left as Jellyfin reports it");
            return;
        }

        if (inner is SteadyGuideManager)
        {
            return;
        }

        field.SetValue(task, new SteadyGuideManager(inner));
    }

    // The guide manager the task ends up calling: Jellyfin's own, with the progress filtered.
    private sealed class SteadyGuideManager : IGuideManager
    {
        private readonly IGuideManager _inner;

        public SteadyGuideManager(IGuideManager inner)
        {
            _inner = inner;
        }

        public GuideInfo GetGuideInfo() => _inner.GetGuideInfo();

        public Task RefreshGuide(IProgress<double> progress, CancellationToken cancellationToken)
            => _inner.RefreshGuide(new SteadyProgress(progress), cancellationToken);
    }

    // Passes a report on only when it is higher than every report before it. One instance per refresh, so each
    // run starts again from zero. Locked because Jellyfin reports from thread-pool callbacks as well as inline.
    private sealed class SteadyProgress : IProgress<double>
    {
        private readonly IProgress<double> _inner;
        private readonly object _gate = new();
        private double _highest;

        public SteadyProgress(IProgress<double> inner)
        {
            _inner = inner;
        }

        public void Report(double value)
        {
            lock (_gate)
            {
                if (value <= _highest)
                {
                    return;
                }

                _highest = value;
                _inner.Report(value);
            }
        }
    }
}
