using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Pty.Unix;

/// <summary>
/// Drains one PTY master from a thread of its own. The read parks in native <c>poll</c> for as
/// long as the agent is idle, and on a pool worker that is a worker the daemon's input writes
/// cannot have: past the pool's worker minimum, every further idle session costs the next
/// keystroke a pool injection.
/// </summary>
sealed class UnixPtyReaderThread {
    const int PollTimeoutMs = 200;
    // A write the child made just before exiting can reach the master a moment after the exit is seen.
    const int DrainPollMs   = 50;
    const int ChunkSize     = 4096;

    static readonly TimeSpan ExitCheckGap = TimeSpan.FromMilliseconds(PollTimeoutMs);
    // A descendant still writing would otherwise keep the drain going for as long as it lives.
    static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(1);

    readonly int                  _masterFd;
    readonly Func<bool>           _childExited;
    readonly TimeProvider         _time;
    readonly CancellationToken    _stop;
    readonly Channel<byte[]>      _chunks  = Channel.CreateUnbounded<byte[]>(new() { SingleWriter = true });
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>One chunk of read-ahead, no more: while the consumer stalls it is the PTY's own
    /// buffer that back-pressures the child, and reading on would move that backlog into the
    /// daemon's memory. A monitor rather than a semaphore so that handing the credit back wakes
    /// the thread directly, without a pool worker in between.</summary>
    readonly object _creditGate = new();
    bool            _credit     = true;

    bool _childGone;
    long _exitCheckedAt;
    long _exitSeenAt;

    /// <param name="childExited">Reaps the child if it has exited. Asked whenever the terminal is idle, and
    /// at least every <see cref="ExitCheckGap"/> while it is not: a descendant that inherited the terminal
    /// keeps it open after the child dies, so no hangup comes, and it may keep writing.</param>
    public UnixPtyReaderThread(int masterFd, int pid, Func<bool> childExited, TimeProvider time, CancellationToken stop) {
        _masterFd      = masterFd;
        _childExited   = childExited;
        _time          = time;
        _exitCheckedAt = time.GetTimestamp();
        _stop          = stop;

        new Thread(Run) { IsBackground = true, Name = $"kcap-pty-reader-{pid}" }.Start();
    }

    /// <summary>Completes once the thread has left the fd for good. Closing the fd earlier frees
    /// its number for the next spawn while this thread may still be about to read from it.</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>The next chunk in arrival order, or <c>null</c> once the stream is over — EOF, a
    /// read error, or the stop token.</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct) {
        while (await _chunks.Reader.WaitToReadAsync(ct)) {
            if (!_chunks.Reader.TryRead(out var chunk)) continue;

            lock (_creditGate) {
                _credit = true;
                Monitor.Pulse(_creditGate);
            }

            return chunk;
        }

        return null;
    }

    void Run() {
        try {
            var buf = new byte[ChunkSize];

            while (TakeCredit()) {
                var bytesRead = ReadChunk(buf);
                if (bytesRead <= 0) break;

                _chunks.Writer.TryWrite(buf.AsSpan(0, bytesRead).ToArray());
            }

            _chunks.Writer.TryComplete();
        } catch (Exception ex) {
            _chunks.Writer.TryComplete(ex);
        } finally {
            _stopped.TrySetResult();
        }
    }

    bool TakeCredit() {
        lock (_creditGate) {
            while (!_credit) {
                if (_stop.IsCancellationRequested) return false;

                Monitor.Wait(_creditGate, PollTimeoutMs);
            }

            _credit = false;

            return true;
        }
    }

    /// <summary>Bytes read, or 0 once the slave side has hung up, the child has exited with nothing left
    /// to read, or the stop token fired. Throws
    /// <see cref="IOException"/> for any other failure: the end of the stream is what tears an agent
    /// down, so a failure that is not a hangup must surface as one rather than pass for an exit.</summary>
    int ReadChunk(byte[] buf) {
        const short hangup = UnixPtyInterop.POLLHUP | UnixPtyInterop.POLLERR;

        var pfd = new UnixPtyInterop.PollFd { fd = _masterFd, events = UnixPtyInterop.POLLIN };

        while (!_stop.IsCancellationRequested) {
            if (_childGone && _time.GetElapsedTime(_exitSeenAt) >= DrainLimit) return 0;

            pfd.revents = 0;
            var pollResult = UnixPtyInterop.poll(ref pfd, 1, _childGone ? DrainPollMs : PollTimeoutMs);

            if (pollResult < 0) {
                var errno = Marshal.GetLastPInvokeError();
                if (UnixPtyInterop.IsTransient(errno)) continue;

                throw Failure("poll", errno);
            }

            if (pollResult == 0) {
                if (_childGone) return 0;

                // Read on until the master has nothing left, so the child's last output is not dropped.
                CheckChildExited();

                continue;
            }

            if ((pfd.revents & UnixPtyInterop.POLLIN) != 0) {
                if (!_childGone && _time.GetElapsedTime(_exitCheckedAt) >= ExitCheckGap) CheckChildExited();

                var bytesRead = (int)UnixPtyInterop.read(_masterFd, buf, buf.Length);
                if (bytesRead >= 0) return bytesRead;

                var errno = Marshal.GetLastPInvokeError();
                if (UnixPtyInterop.IsTransient(errno)) continue;

                // Linux reports the slave's last close to a master read as EIO, not as EOF.
                if (errno == UnixPtyInterop.EIO && (pfd.revents & hangup) != 0) return 0;

                throw Failure("read", errno);
            }

            if ((pfd.revents & hangup) != 0) return 0;

            if ((pfd.revents & UnixPtyInterop.POLLNVAL) != 0) throw Failure("poll", UnixPtyInterop.EBADF);
        }

        return 0;
    }

    void CheckChildExited() {
        _exitCheckedAt = _time.GetTimestamp();

        if (!_childExited()) return;

        _childGone  = true;
        _exitSeenAt = _exitCheckedAt;
    }

    static IOException Failure(string call, int errno) =>
        new($"PTY {call} failed: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
}
