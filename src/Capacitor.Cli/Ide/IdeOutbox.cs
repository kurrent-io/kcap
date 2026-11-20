using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Capacitor.Cli.Ide;

/// <summary>Sends notifications one at a time, in the order they were queued.</summary>
internal sealed class IdeOutbox : IDisposable {
    readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    readonly Func<string, JsonNode, Task> _notify;
    volatile bool _disposed;

    public IdeOutbox(Func<string, JsonNode, Task> notify) {
        _notify = notify;
        _ = PumpAsync();
    }

    /// <param name="stillWanted">Asked again just before the message is written; false drops it.</param>
    public void Send(string method, JsonNode parameters, Func<bool>? stillWanted = null) =>
        _queue.Writer.TryWrite(() => _disposed || stillWanted?.Invoke() == false ? Task.CompletedTask : _notify(method, parameters));

    /// <summary>Completes once everything queued before it has been written or dropped.</summary>
    public Task DrainAsync() {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() => { drained.TrySetResult(); return Task.CompletedTask; })) drained.TrySetResult();

        return drained.Task;
    }

    /// <summary>Nothing is written afterwards, queued or not, except a message already being written.</summary>
    public void Dispose() {
        _disposed = true;
        _queue.Writer.TryComplete();
    }

    async Task PumpAsync() {
        await foreach (var next in _queue.Reader.ReadAllAsync()) {
            try {
                await next();
            } catch (Exception ex) {
                try {
                    await Console.Error.WriteLineAsync($"kcap ide: sending a notification failed: {ex}");
                } catch {
                    // A broken stderr is not a protocol fault.
                }
            }
        }
    }
}
