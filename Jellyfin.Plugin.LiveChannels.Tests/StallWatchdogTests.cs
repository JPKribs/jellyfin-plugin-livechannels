using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the producer stall watchdog: a hung ffmpeg keeps its output pipe open without writing, so the pump's
/// read must time out rather than wait forever with the viewer staring at a frozen channel.
/// </summary>
public class StallWatchdogTests
{

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    [Fact(Timeout = 30000)]
    public async Task Data_IsReturned()
    {
        using var source = new ScriptedStream();
        source.Write(3);

        Assert.Equal(3, await StreamSessionService.ReadOrStallAsync(source, new byte[16], Short, CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task EndOfOutput_IsZero_NotAStall()
    {
        using var source = new ScriptedStream();
        source.End();

        Assert.Equal(0, await StreamSessionService.ReadOrStallAsync(source, new byte[16], Short, CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task AnOpenButSilentSource_IsAStall()
    {
        using var source = new ScriptedStream();
        var watch = Stopwatch.StartNew();

        Assert.Null(await StreamSessionService.ReadOrStallAsync(source, new byte[16], Short, CancellationToken.None));
        Assert.InRange(watch.Elapsed, Short - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));

        // Killing the producer closes its pipe, which completes the read that was abandoned.
        source.End();
    }

    [Fact(Timeout = 30000)]
    public async Task SlowButSteadyOutput_IsNotAStall()
    {
        using var source = new ScriptedStream();
        var buffer = new byte[16];
        // Each chunk arrives a fifth of the way into a two-second limit: slow, but never a stall, with margin
        // enough that a busy test machine cannot turn the wait into one.
        var limit = TimeSpan.FromSeconds(2);
        for (var i = 0; i < 4; i++)
        {
            var pending = StreamSessionService.ReadOrStallAsync(source, buffer, limit, CancellationToken.None);
            await Task.Delay(limit / 5);
            source.Write(1);
            Assert.Equal(1, await pending);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Cancellation_StillWins()
    {
        using var source = new ScriptedStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StreamSessionService.ReadOrStallAsync(source, new byte[16], TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task AFrozenFfmpeg_IsDetected()
    {
        // A real producer frozen mid-stream with SIGSTOP (as a wedged encoder or a stalled network read would
        // leave it): its pipe stays open and silent, so the watchdog must report a stall once the bytes already
        // in the pipe are drained, and the stopped process must still die when killed. The body runs on the
        // thread pool, as the producer pump does in production: disposing a process with an abandoned read from
        // xUnit's own synchronization context deadlocks, which production never does.
        var ff = FfmpegHarness.Instance;
        if (ff is null || OperatingSystem.IsWindows())
        {
            return;
        }

        await Task.Run(async () =>
        {
            var info = new ProcessStartInfo(ff.Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30", "-c:v", "libx264", "-preset", "ultrafast", "-f", "mpegts", "pipe:1" })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            _ = process.StandardError.ReadToEndAsync();
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                var buffer = new byte[81920];
                Assert.True(await StreamSessionService.ReadOrStallAsync(stdout, buffer, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false) > 0);
                Assert.Equal(0, Signal("STOP", process.Id));

                int? read;
                var drains = 0;
                do
                {
                    read = await StreamSessionService.ReadOrStallAsync(stdout, buffer, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    Assert.NotEqual(0, read); // a frozen process never ends its output on its own
                }
                while (read is not null && ++drains < 1000);

                Assert.Null(read);
            }
            finally
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            Assert.True(process.HasExited, "a stopped ffmpeg must still die when killed");
        });
    }

    // Sends a signal straight through libc: spawning a `kill` child and waiting on it does not return on macOS
    // while another child of the test host is stopped.
    private static int Signal(string signal, int pid)
    {
        var number = signal switch
        {
            "STOP" => OperatingSystem.IsMacOS() ? 17 : 19,
            "CONT" => OperatingSystem.IsMacOS() ? 19 : 18,
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
        return Kill(pid, number);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    // A producer's stdout under test control: reads wait until bytes are written or the output is ended.
    private sealed class ScriptedStream : Stream
    {
        private readonly object _gate = new();
        private TaskCompletionSource<int>? _waiting;
        private int _available;
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Write(int count)
        {
            lock (_gate)
            {
                _available += count;
                Release();
            }
        }

        public void End()
        {
            lock (_gate)
            {
                _ended = true;
                Release();
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_available > 0 || _ended)
                {
                    return new ValueTask<int>(Take(buffer.Length));
                }

                _waiting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var max = buffer.Length;
                return new ValueTask<int>(_waiting.Task.ContinueWith(_ => { lock (_gate) { return Take(max); } }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(int max)
        {
            var n = Math.Min(max, _available);
            _available -= n;
            return n;
        }

        private void Release()
        {
            var waiting = _waiting;
            _waiting = null;
            waiting?.TrySetResult(0);
        }
    }
}
