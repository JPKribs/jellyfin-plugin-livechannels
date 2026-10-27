using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Keeps a record of every producer ffmpeg that failed (no output, hung, or an error exit): the exact command,
/// what happened, and ffmpeg's own error output, one file per failure next to Jellyfin's logs. A session's own
/// ffmpeg log is deleted with the session, so without this a channel that fell to standby overnight leaves
/// nothing to diagnose it from. Only the newest reports are kept, so a failure storm cannot fill the disk.
/// </summary>
public sealed class FailureReports
{
    private const string Prefix = "livechannels-failure-";
    private static readonly SearchValues<char> ShellSpecial = SearchValues.Create(" '\"()|;&");
    private readonly string _directory;
    private readonly int _keep;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FailureReports"/> class.
    /// </summary>
    /// <param name="directory">Where reports are written (Jellyfin's log directory).</param>
    /// <param name="keep">How many of the newest reports to keep.</param>
    public FailureReports(string directory, int keep = 25)
    {
        _directory = directory;
        _keep = Math.Max(1, keep);
    }

    /// <summary>
    /// Writes a failure report and prunes old ones. Best effort: diagnostics must never break the stream they
    /// describe, so any I/O failure is swallowed.
    /// </summary>
    /// <param name="label">What was playing (the item title, or the channel for a continuous run).</param>
    /// <param name="args">The ffmpeg arguments that ran.</param>
    /// <param name="outcome">What happened, in a sentence.</param>
    /// <param name="stderr">ffmpeg's error output.</param>
    /// <param name="nowUtc">The time of the failure.</param>
    /// <returns>The report's path, or <c>null</c> when it could not be written.</returns>
    public string? Write(string label, IReadOnlyList<string> args, string outcome, string stderr, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var stamp = nowUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture);
                var path = Path.Combine(_directory, Prefix + stamp + "-" + Sanitize(label) + ".log");

                var text = new StringBuilder()
                    .Append("Live Channels producer failure\n")
                    .Append("Time (UTC): ").Append(nowUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)).Append('\n')
                    .Append("Playing: ").Append(label).Append('\n')
                    .Append("Outcome: ").Append(outcome).Append("\n\n")
                    .Append("Command:\nffmpeg ").Append(string.Join(' ', args.Select(Quote))).Append("\n\n")
                    .Append("ffmpeg error output:\n").Append(string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim()).Append('\n');
                File.WriteAllText(path, text.ToString());

                foreach (var old in Directory.GetFiles(_directory, Prefix + "*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(_keep))
                {
                    File.Delete(old);
                }

                return path;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A file-name-safe, bounded version of the label.
    private static string Sanitize(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((label ?? string.Empty).Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    // Quotes an argument containing spaces or quotes so the command can be pasted into a shell.
    private static string Quote(string arg)
        => arg.Length > 0 && arg.AsSpan().IndexOfAny(ShellSpecial) < 0 ? arg : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
