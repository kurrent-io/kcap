using System.Globalization;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// A terminal Claude session steered through the kcap-steering mod. The daemon started no process and holds no
/// terminal, so this offers no output, keys, raw input or resize, and stopping it touches nothing in the process: no
/// signal, no write. What it carries is the dispatches the server pushed for it, held in memory until the server
/// confirms a handover to the mod, each with the connection it came on, since nothing pushed on another connection may
/// be handed over.
/// </summary>
/// <param name="connectionId">The server connection now open, read as each dispatch arrives.</param>
internal sealed class ModAttachedAgentRuntime(ModAttachmentRecord attachment, Func<string?> connectionId, TimeProvider time) : IHostedAgentRuntime {
    readonly Lock _gate = new();
    readonly List<(Guid DispatchId, string Text, string? ConnectionId)> _held = [];
    ModAttachmentRecord _attachment = attachment;
    long _lastBeat = time.GetTimestamp();
    int _handingOver;

    public ModAttachmentRecord Attachment { get { lock (_gate) return _attachment; } }

    public string AgentId => Attachment.AgentId;

    public string ProcessKey => Attachment.ProcessKey;

    public string Vendor => "claude";

    public string RuntimeTransport => ServerConnection.ModTransport;

    /// <summary>The pid the mod's process key starts with; never signalled.</summary>
    public int Pid => PidOf(Attachment.ProcessKey);

    public bool HasExited => false;

    public int? ExitCode => null;

    public bool EmitsTerminalOutput => false;

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => AsyncEnumerable.Empty<byte[]>();

    /// <summary>Refused: only a dispatch the server can confirm handing over may reach the mod.</summary>
    public Task SendUserInputAsync(string text) =>
        throw new InputNotAdmittedException("an attached session takes only server dispatches, through its mod");

    /// <summary>Holds the dispatch for the mod and returns at once: it moves only when the mod asks and the server
    /// confirms, never from the server's push.</summary>
    public Task SendDispatchedInputAsync(string text, Guid dispatchId, bool waitForWrite) {
        var arrivedOn = connectionId();

        lock (_gate) {
            if (_held.All(h => h.DispatchId != dispatchId)) _held.Add((dispatchId, text, arrivedOn));
        }

        return Task.CompletedTask;
    }

    public Task SendSpecialKeyAsync(string key) =>
        Task.FromException(new NotSupportedException("an attached session takes no keys: it is steered through its mod"));

    public Task SendRawInputAsync(byte[] data) =>
        Task.FromException(new NotSupportedException("an attached session takes no raw input: it is steered through its mod"));

    public void Resize(ushort cols, ushort rows) { }

    public Task RequestGracefulStopAsync() => Task.CompletedTask;

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public ValueTask DisposeAsync() {
        DropHeld();

        return ValueTask.CompletedTask;
    }

    /// <summary>The same process attached again: what it holds stays, since the server keeps what was pushed on this
    /// connection.</summary>
    public void Reattach(ModAttachmentRecord again) {
        lock (_gate) _attachment = again;
        Beat();
    }

    public void Beat() => Volatile.Write(ref _lastBeat, time.GetTimestamp());

    public TimeSpan SilentFor => time.GetElapsedTime(Volatile.Read(ref _lastBeat));

    /// <summary>The oldest dispatch held on <paramref name="current"/>; any held from another connection is dropped
    /// first, since the server rejected it with that connection.</summary>
    public (Guid DispatchId, string Text)? OldestHeld(string? current) {
        lock (_gate) {
            _held.RemoveAll(h => h.ConnectionId is null || h.ConnectionId != current);

            return _held.Count == 0 ? null : (_held[0].DispatchId, _held[0].Text);
        }
    }

    public void RemoveHeld(Guid dispatchId) {
        lock (_gate) _held.RemoveAll(h => h.DispatchId == dispatchId);
    }

    public int HeldCount { get { lock (_gate) return _held.Count; } }

    public void DropHeld() {
        lock (_gate) _held.Clear();
    }

    /// <summary>One handover at a time: two polls racing would ask the server twice for one dispatch and could hand it
    /// to the mod twice.</summary>
    public bool TryBeginHandover() => Interlocked.CompareExchange(ref _handingOver, 1, 0) == 0;

    public void EndHandover() => Volatile.Write(ref _handingOver, 0);

    /// <summary>The pid a process key built as <c>pid:start time</c> starts with; 0 for any other shape.</summary>
    internal static int PidOf(string processKey) {
        var colon = processKey.IndexOf(':');

        return colon > 0 && int.TryParse(processKey.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
    }
}
