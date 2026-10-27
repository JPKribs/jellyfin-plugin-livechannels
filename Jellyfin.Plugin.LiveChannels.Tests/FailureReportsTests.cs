using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// A failed producer leaves a report next to Jellyfin's logs (the command, what happened, ffmpeg's errors), and
/// only the newest reports are kept.
/// </summary>
public sealed class FailureReportsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lc-reports-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Report_HoldsTheCommandOutcomeAndErrors()
    {
        var path = new FailureReports(_dir).Write("Bob's Show: S01E02", new[] { "-i", "/media/Bob's Show/a b.mkv", "pipe:1" }, "produced no output (exit code 1)", "[in#0] Invalid data found\n", T0);

        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_dir, "livechannels-failure-20261027T050000.000Z-"), path, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", Path.GetFileName(path), StringComparison.Ordinal);
        var text = File.ReadAllText(path!);
        Assert.Contains("Playing: Bob's Show: S01E02", text, StringComparison.Ordinal);
        Assert.Contains("Outcome: produced no output (exit code 1)", text, StringComparison.Ordinal);
        Assert.Contains("ffmpeg -i '/media/Bob'\\''s Show/a b.mkv' pipe:1", text, StringComparison.Ordinal);
        Assert.Contains("[in#0] Invalid data found", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheNewestReportsAreKept()
    {
        var reports = new FailureReports(_dir, keep: 3);
        for (var i = 0; i < 6; i++)
        {
            reports.Write("item " + i, Array.Empty<string>(), "hung", string.Empty, T0.AddMinutes(i));
        }

        var kept = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(3, kept.Count);
        Assert.All(kept, n => Assert.True(n!.Contains("item_3", StringComparison.Ordinal) || n.Contains("item_4", StringComparison.Ordinal) || n.Contains("item_5", StringComparison.Ordinal), n));
    }

    [Fact]
    public void AnUnwritableDirectory_IsNotAnError()
    {
        File.WriteAllText(_dir, "a file where the directory should be");
        try
        {
            Assert.Null(new FailureReports(Path.Combine(_dir, "sub")).Write("x", Array.Empty<string>(), "hung", string.Empty, T0));
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    [Theory]
    [InlineData(5000L, 0, false, false, null)] // a clean run
    [InlineData(0L, -1, false, true, null)] // cancelled by the session: not a failure
    [InlineData(5000L, -1, true, true, null)] // cancelled even if the watchdog also fired
    [InlineData(0L, 1, false, false, "produced no output (exit code 1)")]
    [InlineData(5000L, 187, false, false, "exited with code 187 after 5000 bytes")]
    [InlineData(5000L, -1, true, false, "hung: stopped writing output after 5000 bytes and was killed")]
    public void Outcome_NamesOnlyRealFailures(long total, int exitCode, bool stalled, bool cancelled, string? expected)
        => Assert.Equal(expected, StreamSessionService.FailureOutcome(total, exitCode, stalled, cancelled));

    [Fact]
    public void ARunKilledOnlyForASlowExit_IsNotAFailure()
    {
        // It finished its output, then a hardware encoder took longer than the grace to tear down and was killed:
        // the kill's exit code (137) says nothing about the item, which played in full.
        Assert.Null(StreamSessionService.FailureOutcome(5000, 137, false, false, slowExitAfterOutput: true));

        // A run that produced nothing is a failure however it ended.
        Assert.NotNull(StreamSessionService.FailureOutcome(0, 137, false, false, slowExitAfterOutput: true));
    }
}
