using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>Where the gateway descriptor JSON — scheme, host, port, token — is read from. The gateway reads
/// it live on every call, because the gateway rewrites it with a new port and token on each restart.</summary>
public abstract class GrokBotDescriptor {
    public abstract string Describe { get; }

    /// <summary>The descriptor JSON, read now. Throws when it cannot be obtained.</summary>
    public abstract Task<string> ReadJsonAsync(CancellationToken ct);
}

/// <summary>The descriptor read straight from a file the watcher's own user can open — the desktop app, or a
/// cloud computer that still leaves the descriptor readable.</summary>
public sealed class GrokBotFileDescriptor(string path) : GrokBotDescriptor {
    public override string Describe => $"the gateway at {path}";

    // Shared read: the gateway rewrites the descriptor on each restart, and the read must not deny that write.
    public override Task<string> ReadJsonAsync(CancellationToken ct) => Task.FromResult(File.ReadAllTextShared(path));
}
