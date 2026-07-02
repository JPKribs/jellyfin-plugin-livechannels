using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Live, mutable statistics for one active channel stream. The producer writes to it as ffmpeg reports
/// progress and the configuration page's Sessions tab reads it, so the value is exchanged atomically
/// because the two run on different threads. Also carries the session's ffmpeg diagnostic log: every process
/// the session spawns appends its command line and exit summary, giving the Sessions tab a reviewable history.
/// </summary>
public sealed class SessionStats
{
    // The log lives inside the session directory, so it is deleted with the session (close, kill, orphan
    // sweep, and the cleanup task all remove the whole directory) — no separate log cleanup exists or is
    // needed. Bounded: when it outgrows the cap, the OLDER half is dropped (recent history is what diagnoses
    // a live problem), so a per-item channel running for days cannot grow it without limit.
    private const long MaxLogBytes = 1_500_000;

    private readonly object _logLock = new();
    private long _speedBits;

    /// <summary>
    /// Gets or sets the most recent encode speed as a realtime multiple, parsed from the producer ffmpeg's
    /// progress output. 1.0 is realtime; below that the box is not keeping up. Zero until ffmpeg reports one.
    /// </summary>
    public double Speed
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
        set => Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>
    /// Gets or sets the path of the session's ffmpeg diagnostic log (inside the session directory), or
    /// <c>null</c> to disable logging (e.g. tests).
    /// </summary>
    public string? LogPath { get; set; }

    /// <summary>
    /// Appends a timestamped block to the session's ffmpeg log. Best effort by design: diagnostics must never
    /// be able to break the stream they describe.
    /// </summary>
    /// <param name="text">The block to append (a command line, or an exit summary with its stderr tail).</param>
    public void AppendLog(string text)
    {
        var path = LogPath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            lock (_logLock)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    var existing = File.ReadAllText(path);
                    var keepFrom = existing.IndexOf('\n', existing.Length / 2);
                    File.WriteAllText(path, "[… older log trimmed …]\n" + (keepFrom > 0 ? existing[keepFrom..] : string.Empty));
                }

                var stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(path, "[" + stamp + "] " + text.Trim() + "\n\n");
            }
        }
        catch (Exception)
        {
            // Diagnostics only; the stream must not care.
        }
    }
}
