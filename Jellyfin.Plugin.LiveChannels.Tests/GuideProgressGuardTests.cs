using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="GuideProgressGuard"/>: once installed, the Refresh Guide task only ever reports a
/// percentage higher than the last, and a server without the task in its expected shape is left alone.
/// </summary>
public class GuideProgressGuardTests
{
    [Fact]
    public async Task Installed_ProgressNeverGoesBackwards()
    {
        // The sequence Jellyfin reports with two services: the first fills to 50, the second restarts, then
        // each cleanup pass restarts again.
        var task = new FakeRefreshGuideTask(new FakeGuideManager(10, 50, 5, 30, 50, 100, 0, 50, 100, 25, 100));
        await StartGuard(task);

        var seen = new Recorder();
        await task.ExecuteAsync(seen, CancellationToken.None);

        Assert.Equal(new double[] { 10, 50, 100 }, seen.Values);
    }

    [Fact]
    public async Task Installed_EachRunStartsFromZero()
    {
        var task = new FakeRefreshGuideTask(new FakeGuideManager(40, 100));
        await StartGuard(task);

        var first = new Recorder();
        await task.ExecuteAsync(first, CancellationToken.None);
        var second = new Recorder();
        await task.ExecuteAsync(second, CancellationToken.None);

        Assert.Equal(new double[] { 40, 100 }, second.Values);
    }

    [Fact]
    public async Task StartedTwice_WrapsOnce()
    {
        var task = new FakeRefreshGuideTask(new FakeGuideManager(10, 5, 20));
        await StartGuard(task);
        var wrapped = task.GuideManager;
        await StartGuard(task);

        Assert.Same(wrapped, task.GuideManager);
    }

    [Fact]
    public async Task TaskMissing_DoesNotThrow()
    {
        var manager = Substitute.For<ITaskManager>();
        manager.ScheduledTasks.Returns(Array.Empty<IScheduledTaskWorker>());

        await new GuideProgressGuard(manager, NullLogger<GuideProgressGuard>.Instance).StartAsync(CancellationToken.None);
    }

    private static Task StartGuard(IScheduledTask task)
    {
        var worker = Substitute.For<IScheduledTaskWorker>();
        worker.ScheduledTask.Returns(task);
        var manager = Substitute.For<ITaskManager>();
        manager.ScheduledTasks.Returns(new[] { worker });
        return new GuideProgressGuard(manager, NullLogger<GuideProgressGuard>.Instance).StartAsync(CancellationToken.None);
    }

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    private sealed class FakeGuideManager : IGuideManager
    {
        private readonly double[] _reports;

        public FakeGuideManager(params double[] reports)
        {
            _reports = reports;
        }

        public GuideInfo GetGuideInfo() => new();

        public Task RefreshGuide(IProgress<double> progress, CancellationToken cancellationToken)
        {
            foreach (var report in _reports)
            {
                progress.Report(report);
            }

            return Task.CompletedTask;
        }
    }

    // Shaped like Jellyfin's RefreshGuideScheduledTask: the guide manager sits in a private readonly field.
    private sealed class FakeRefreshGuideTask : IScheduledTask
    {
        private readonly IGuideManager _guideManager;

        public FakeRefreshGuideTask(IGuideManager guideManager)
        {
            _guideManager = guideManager;
        }

        public IGuideManager GuideManager => _guideManager;

        public string Name => "Refresh Guide";

        public string Key => "RefreshGuide";

        public string Description => string.Empty;

        public string Category => "Live TV";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _guideManager.RefreshGuide(progress, cancellationToken);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }
}
