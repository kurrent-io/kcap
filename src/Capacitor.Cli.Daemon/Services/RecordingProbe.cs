using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Whether the server on the daemon's main connection can stop a source. A version can't say: beta, dev and
/// self-hosted servers send none. Calling <c>WatcherDetach</c> for a key nobody holds does nothing on a server that has
/// it and fails as an unknown method on one that doesn't, and only that failure is a no. The answer goes with the
/// connection, and no answer reads as no, so nothing has to wait for one.</summary>
internal sealed class RecordingProbe {
    readonly Lock _gate = new();

    TaskCompletionSource<bool> _answer = NewAnswer();

    /// <summary>What the server answered on this connection, or null until it has.</summary>
    public bool? Answer {
        get {
            lock (_gate) return _answer.Task.IsCompleted ? _answer.Task.Result : null;
        }
    }

    /// <summary>Completes when the server has answered.</summary>
    public Task<bool> Answered {
        get {
            lock (_gate) return _answer.Task;
        }
    }

    /// <summary>Drops the answer of a connection that went away. An ask still out is left be: it goes with that
    /// connection, and the next one's answer settles whoever waits.</summary>
    public void Forget() {
        lock (_gate) {
            if (_answer.Task.IsCompleted) _answer = NewAnswer();
        }
    }

    public void Report(bool records) {
        lock (_gate) _answer.TrySetResult(records);
    }

    /// <summary>The server's answer, or null when <paramref name="detach"/> failed in a way that says nothing.</summary>
    /// <param name="detach">Calls <c>WatcherDetach</c> for a key nobody holds.</param>
    public async Task<bool?> AskAsync(Func<Task> detach) {
        bool answer;

        try {
            await detach();
            answer = true;
        } catch (HubException ex) when (IsUnknownMethod(ex)) {
            answer = false;
        } catch (Exception) {
            return null;
        }

        Report(answer);

        return answer;
    }

    // SignalR's two wordings: the first fails binding the call's arguments, the second finding its target.
    static bool IsUnknownMethod(HubException ex) =>
        ex.Message.Contains("Method does not exist", StringComparison.Ordinal) || ex.Message.Contains("Unknown hub method", StringComparison.Ordinal);

    static TaskCompletionSource<bool> NewAnswer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
