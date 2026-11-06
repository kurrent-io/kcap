namespace Capacitor.Cli;

/// <summary>What a hook asks of the daemon recording a source.</summary>
public interface IDaemonRecordingClient {
    /// <summary>Asks the daemon to make the final drain of the source <paramref name="key"/> names, touching no other
    /// source. Answers whether it finished; a hook that stops waiting has still asked, and the daemon still finishes.</summary>
    Task<bool> FinishSourceAsync(string key, CancellationToken ct);
}
