using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Reads a session's rolling HLS segments (seg0.ts, seg1.ts, ...) back as one continuous MPEG-TS byte stream.
/// The segmenter's timestamps are already continuous across segments and items, and MPEG-TS concatenates by
/// plain byte append, so sequential reads of consecutive segments ARE the live transport stream. Jellyfin wraps
/// this in its ProgressiveFileStream, which retries a zero-length read every 50ms (for up to 30s) — so at the
/// live edge this stream simply returns 0 and lets the wrapper wait for the segmenter to finish the next segment.
/// Pure file-system logic (no Jellyfin dependencies) so the tail-following behaviour can be unit-tested directly.
/// </summary>
public sealed class SegmentConcatStream : Stream
{
    private readonly string _directory;
    private readonly int _startBehind;
    private readonly int _holdBehind;
    private readonly Action<string>? _log;
    private FileStream? _segment;
    private long _number = -1;
    private long _firstNumber = -1;
    private long _bytesServed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentConcatStream"/> class.
    /// </summary>
    /// <param name="directory">The session directory holding the rolling seg&lt;n&gt;.ts files.</param>
    /// <param name="log">Optional sink for lifecycle messages (start position, window jumps, close summary).</param>
    /// <param name="startBehind">How many segments behind the newest to start, clamped to what the window still
    /// holds. Everything between the start position and the held-back edge is served at I/O speed, giving the
    /// consumer an instant opening backlog.</param>
    /// <param name="holdBehind">How many of the newest segments to withhold. Live HLS players sync a fixed few
    /// segments behind whatever edge the delivery remux exposes, so serving right up to the producer's newest
    /// segment puts every viewer one encoder hiccup from a stall; holding the edge back keeps that many segments
    /// in reserve, and producer gaps shorter than the reserve are absorbed invisibly. Clamped so at least the
    /// oldest available segment is always servable (a brand-new session must still feed the probe).</param>
    public SegmentConcatStream(string directory, Action<string>? log = null, int startBehind = 2, int holdBehind = 0)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(startBehind);
        ArgumentOutOfRangeException.ThrowIfNegative(holdBehind);
        _directory = directory;
        _log = log;
        _startBehind = startBehind;
        _holdBehind = holdBehind;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_segment is not null)
            {
                int read;
                try
                {
                    read = _segment.Read(buffer);
                }
                catch (IOException)
                {
                    read = 0;
                }

                if (read > 0)
                {
                    _bytesServed += read;
                    return read;
                }

                // The segmenter's temp_file flag renames each segment into place only when complete, so a
                // visible segment never grows: end of file here is final, move on to the next segment.
                _segment.Dispose();
                _segment = null;
                _number++;
            }

            if (!TryOpenNext())
            {
                // At the live edge (or the session was torn down). The ProgressiveFileStream wrapper retries;
                // once the directory is gone for good its timeout ends the response.
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Reads never block (a zero return is the wait signal), so the synchronous path is the async path.
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _segment?.Dispose();
            _segment = null;

            // The close summary is the reader's whole story in one line; "0 bytes" here means the consumer
            // connected but never received data, which is the signature of a delivery problem.
            _log?.Invoke(_firstNumber < 0
                ? "closed without serving any data (no segments were available)"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"closed after seg{_firstNumber}..seg{_number} ({_bytesServed} bytes served)"));
        }

        base.Dispose(disposing);
    }

    private static long ParseSegmentNumber(string file)
    {
        // "seg123.ts" -> 123; anything else (foreign .ts files, malformed names) is ignored.
        var name = Path.GetFileNameWithoutExtension(file.AsSpan());
        if (name.Length > 3 && long.TryParse(name[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return -1;
    }

    private bool TryOpenNext()
    {
        // Two attempts cover the one race that matters: a segment listed (or seen) as present can age out of the
        // rolling window before the open lands; the second pass re-lists and jumps to the oldest survivor.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var numbers = ListSegmentNumbers();
            if (numbers.Count == 0)
            {
                return false;
            }

            // The newest segment this reader is allowed to serve: the true edge minus the hold-back, clamped so
            // the oldest segment is always servable (a session too young to satisfy the full hold-back must
            // still feed the probe and the delivery's first bytes).
            var maxServable = numbers[^1] - Math.Min(_holdBehind, numbers[^1] - numbers[0]);

            if (_number < 0)
            {
                // First read: start behind the newest, clamped both to what the window still holds and to the
                // held-back edge.
                _number = Math.Min(Math.Max(numbers[0], numbers[^1] - _startBehind), maxServable);
                _firstNumber = _number;
                _log?.Invoke(string.Create(
                    CultureInfo.InvariantCulture,
                    $"starting at seg{_number} (window seg{numbers[0]}..seg{numbers[^1]}, serving up to seg{maxServable})"));
            }
            else if (_number > maxServable)
            {
                // At the held-back edge: the next segment may already exist, but serving it would put the
                // consumer on the producer's heels, where any encoder hiccup becomes a visible stall. Wait for
                // the producer to move further ahead instead.
                return false;
            }
            else if (!File.Exists(SegmentPath(_number)))
            {
                // Already deleted from the rolling window (reader fell behind): jump forward to the oldest
                // segment that still exists, respecting the held-back edge.
                var index = numbers.FindIndex(n => n > _number);
                if (index < 0 || numbers[index] > maxServable)
                {
                    return false;
                }

                _log?.Invoke(string.Create(
                    CultureInfo.InvariantCulture,
                    $"fell off the rolling window at seg{_number}; jumping to seg{numbers[index]}"));
                _number = numbers[index];
            }

            try
            {
                // ReadWrite | Delete sharing: the segmenter may rename the NEXT segment beside this one and will
                // eventually delete this one out of the window; an already-open handle must survive both.
                _segment = new FileStream(SegmentPath(_number), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                return true;
            }
            catch (IOException)
            {
                // Deleted between the existence check and the open; loop once to re-resolve.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private string SegmentPath(long number)
        => Path.Combine(_directory, "seg" + number.ToString(CultureInfo.InvariantCulture) + ".ts");

    private List<long> ListSegmentNumbers()
    {
        var numbers = new List<long>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "seg*.ts"))
            {
                var number = ParseSegmentNumber(file);
                if (number >= 0)
                {
                    numbers.Add(number);
                }
            }
        }
        catch (IOException)
        {
            // The session directory was torn down; report nothing left.
        }
        catch (UnauthorizedAccessException)
        {
        }

        numbers.Sort();
        return numbers;
    }
}
