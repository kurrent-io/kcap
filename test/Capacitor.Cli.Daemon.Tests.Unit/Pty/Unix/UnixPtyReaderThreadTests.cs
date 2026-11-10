using System.IO.Pipes;
using System.Text;
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
            }, stop.Token);

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
}
