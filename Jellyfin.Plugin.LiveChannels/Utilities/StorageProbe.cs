using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Checks whether media files exist without letting a hung network mount hang the stream. On a hard-mounted
/// NFS or SMB share whose server has gone, a plain existence check blocks indefinitely, before any ffmpeg (and so
/// any watchdog) exists, and the channel never reaches standby. Each check here gives up after a bound; the first
/// that does marks the storage hung, and every later check on this probe answers "missing" at once, so a caller
/// walking a playlist stops after one wait instead of one per item.
/// </summary>
internal sealed class StorageProbe
{
    /// <summary>
    /// The default bound on one existence check: well past a NAS or USB disk waking from spin-down (10 to 15 seconds
    /// is common, and the tune-in card covers it), well short of the minutes a frozen picture would otherwise last.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many timed-out checks may still be waiting on their storage, across every probe. Each one holds a
    /// thread-pool thread until its mount answers, and every session's standby retry starts a new probe, so without
    /// a limit a mount that stays hung takes one more thread per retry and the whole server slows down.
    /// </summary>
    public const int MaxAbandonedChecks = 4;

    private static readonly Waiting Shared = new();

    private readonly Waiting _waiting;

    private readonly TimeSpan _timeout;
    private readonly Func<string, bool> _exists;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageProbe"/> class.
    /// </summary>
    /// <param name="timeout">How long one check may take, or <c>null</c> for <see cref="DefaultTimeout"/>.</param>
    /// <param name="exists">The underlying check (tests substitute one that hangs).</param>
    /// <param name="waiting">The count of timed-out checks this probe shares, or <c>null</c> for the one every probe in the server shares.</param>
    public StorageProbe(TimeSpan? timeout = null, Func<string, bool>? exists = null, Waiting? waiting = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _exists = exists ?? File.Exists;
        _waiting = waiting ?? Shared;
    }

    /// <summary>Gets a value indicating whether a check on this probe timed out.</summary>
    public bool Hung { get; private set; }

    /// <summary>Whether the file exists; <c>false</c> when the check timed out or the storage is already hung.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether it exists.</returns>
    public bool Exists(string path)
    {
        if (Hung)
        {
            return false;
        }

        // A full count of waiting checks means hung storage, so no new thread is taken.
        if (_waiting.Count >= MaxAbandonedChecks)
        {
            Hung = true;
            return false;
        }

        // The abandoned check keeps its thread until the mount answers; there is no way to cancel a blocked stat,
        // which is why the first timeout ends all further checks on this probe.
        var check = Task.Run(() => _exists(path));
        if (check.Wait(_timeout))
        {
            return check.Result;
        }

        // Counted until the mount answers, so checks start again once the storage is back.
        var waiting = _waiting;
        waiting.Add(1);
        _ = check.ContinueWith(
            done =>
            {
                waiting.Add(-1);
                _ = done.Exception;
            },
            TaskScheduler.Default);

        Hung = true;
        return false;
    }

    /// <summary>Counts the timed-out checks still waiting on their storage, shared by every probe given it.</summary>
    internal sealed class Waiting
    {
        private int _count;

        /// <summary>Gets how many timed-out checks are still waiting.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Adds to the count.</summary>
        /// <param name="change">One for a check that timed out, minus one for a check that finished.</param>
        public void Add(int change) => Interlocked.Add(ref _count, change);
    }
}
