using System.IO.Pipes;
using System.Text;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

public class UnixPtyReaderThreadTests {
    /// <summary>Output the child wrote after the reader's last idle poll, just before its exit was seen, is read
    /// before the stream ends. The exit check itself writes it, so it lands in exactly that window every run.</summary>
    [Test]
    public async Task Output_written_just_before_the_exit_is_seen_still_reaches_the_reader() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var pipe     = new AnonymousPipeServerStream(PipeDirection.In);
        var       readFd   = (int)pipe.SafePipeHandle.DangerousGetHandle();
        var       writeFd  = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
        var       lastWord = Encoding.UTF8.GetBytes("Error: auth expired");
        var       checks   = 0;

        using var stop   = new CancellationTokenSource();
        var       reader = new UnixPtyReaderThread(readFd, 0, () => {
                if (Interlocked.Increment(ref checks) == 1) UnixPtyInterop.write(writeFd, lastWord, lastWord.Length);

                return true;
            }, TimeProvider.System, stop.Token);

        try {
            using var bound  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var       output = new StringBuilder();
            while (await reader.ReadAsync(bound.Token) is { } chunk) output.Append(Encoding.UTF8.GetString(chunk));

            await Assert.That(output.ToString()).IsEqualTo("Error: auth expired");
        } finally {
            await stop.CancelAsync();
            await reader.Stopped;
        }
    }

    /// <summary>A descendant that keeps writing faster than the idle poll leaves the terminal never idle, and the stream
    /// still ends once the child has gone: its exit is checked while output flows, and the drain after it is bounded.</summary>
    [Test]
    public async Task Output_ends_after_the_exit_though_a_descendant_never_stops_writing() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var pipe    = new AnonymousPipeServerStream(PipeDirection.In);
        var       readFd  = (int)pipe.SafePipeHandle.DangerousGetHandle();
        var       writeFd = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
        var       tick    = "."u8.ToArray();

        using var stop    = new CancellationTokenSource();
        var       writing = Task.Factory.StartNew(() => {
            while (!stop.IsCancellationRequested) {
                UnixPtyInterop.write(writeFd, tick, tick.Length);
                Thread.Sleep(5);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var reader = new UnixPtyReaderThread(readFd, 0, () => true, TimeProvider.System, stop.Token);

        try {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var       ended = false;
            try {
                while (await reader.ReadAsync(bound.Token) is not null) { }
                ended = true;
            } catch (OperationCanceledException) when (bound.IsCancellationRequested) {
                // The stream never ended, which is the failure the assertion names.
            }

            await Assert.That(ended).IsTrue();
        } finally {
            await stop.CancelAsync();
            await reader.Stopped;
            await writing;
        }
    }

    /// <summary>Output the child left before exiting is all read however slowly the consumer takes it: the drain
    /// after the exit bounds reading, not the wait for the consumer. The output spans several chunks, and the
    /// consumer takes longer than the drain limit over each.</summary>
    [Test]
    public async Task A_slow_consumer_still_gets_all_the_output_left_at_the_exit() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        await using var pipe = new AnonymousPipeServerStream(PipeDirection.In);
        var readFd  = (int)pipe.SafePipeHandle.DangerousGetHandle();
        var writeFd = (int)pipe.ClientSafePipeHandle.DangerousGetHandle();
        var left    = new byte[5 * 4096];
        Array.Fill(left, (byte)'x');

        using var stop   = new CancellationTokenSource();
        var       reader = new UnixPtyReaderThread(readFd, 0, () => true, TimeProvider.System, stop.Token);

        // Written beside the reader: a pipe smaller than the output would block a write made ahead of it.
        var writing = Task.Factory.StartNew(() => {
            for (var at = 0; at < left.Length;) {
                var wrote = (int)UnixPtyInterop.write(writeFd, left.AsSpan(at), left.Length - at);
                if (wrote <= 0) return;

                at += wrote;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var       read  = 0;
            while (await reader.ReadAsync(bound.Token) is { } chunk) {
                read += chunk.Length;
                await Task.Delay(PtyExitWatch.DrainLimit + TimeSpan.FromMilliseconds(300));
            }

            await Assert.That(read).IsEqualTo(left.Length);
        } finally {
            await stop.CancelAsync();
            await reader.Stopped;
            // Closing the read end fails a write still blocked on a full pipe.
            await pipe.DisposeAsync();
            await writing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
