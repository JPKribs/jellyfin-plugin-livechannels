using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
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
public sealed class GuideProgressGuard : IHostedService, IDisposable
{
    private const string RefreshGuideKey = "RefreshGuide";

    private readonly ITaskManager _taskManager;
    private readonly ILogger<GuideProgressGuard> _logger;
    private readonly object _gate = new();
    private bool _settled;
    private bool _subscribed;

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
        // Jellyfin starts hosted services before it registers its scheduled tasks, so the task is usually not
        // there yet. Every run raises TaskExecuting before the task's ExecuteAsync, so the guard goes in on the
        // first run's event, in time for that run.
        try
        {
            lock (_gate)
            {
                if (!_settled && !TryInstall(_taskManager.ScheduledTasks.Select(worker => worker.ScheduledTask)) && !_subscribed)
                {
                    _taskManager.TaskExecuting += OnTaskExecuting;
                    _subscribed = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not steady the Refresh Guide progress; the guide refresh itself is unaffected");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Unsubscribe();

    // Runs inside Jellyfin's task start, before the task itself, so it must never throw.
    private void OnTaskExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> e)
    {
        try
        {
            lock (_gate)
            {
                if (!_settled && e?.Argument?.ScheduledTask is { } task)
                {
                    TryInstall(new[] { task });
                }

                if (_settled)
                {
                    UnsubscribeLocked();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not steady the Refresh Guide progress; the guide refresh itself is unaffected");
        }
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            UnsubscribeLocked();
        }
    }

    private void UnsubscribeLocked()
    {
        if (_subscribed)
        {
            _taskManager.TaskExecuting -= OnTaskExecuting;
            _subscribed = false;
        }
    }

    // Returns true once the Refresh Guide task has been dealt with (wrapped, already wrapped, or found in a shape
    // the guard does not understand), and false while the task has not been seen yet. Never throws: the guard is
    // cosmetic, and a Jellyfin whose task is shaped differently must still refresh its guide exactly as it would
    // without the plugin.
    private bool TryInstall(IEnumerable<IScheduledTask?> tasks)
    {
        try
        {
            var task = tasks.FirstOrDefault(t => t is not null && string.Equals(t.Key, RefreshGuideKey, StringComparison.Ordinal));
            if (task is null)
            {
                return false;
            }

            _settled = true;
            Install(task);
        }
        catch (Exception ex)
        {
            _settled = true;
            _logger.LogWarning(ex, "Live Channels: could not steady the Refresh Guide progress; the guide refresh itself is unaffected");
        }

        return _settled;
    }

    // The task holds its guide manager in a private field and Jellyfin registers its own guide manager after
    // plugin services, so there is no registration to override: the field is the only place to step in. It is
    // found by type, not name, so a rename upstream does not break it.
    private void Install(IScheduledTask task)
    {
        var field = task.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(IGuideManager));

        if (field is null || field.GetValue(task) is not IGuideManager inner)
        {
            _logger.LogInformation("Live Channels: the Refresh Guide task was not found in its expected shape; its progress is left as Jellyfin reports it");
            return;
        }

        if (inner is SteadyGuideManager)
        {
            return;
        }

        field.SetValue(task, new SteadyGuideManager(inner));
        _logger.LogDebug("Live Channels: the Refresh Guide progress is now steadied");
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
