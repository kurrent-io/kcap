using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>
/// Stable per-machine identifier: lets the server prove that a daemon reconnecting
/// under a given repo path is actually running on the machine it claims, rather than trusting a
/// path string alone. Persisted once to <c>machine.json</c> in the CLI's config directory
/// (the <see cref="ConfigRoot"/> it is handed — the same one <c>Auth.TokenStore</c> takes) so every
/// process on this machine — hooks, watcher, daemon, MCP — resolves the same file and reports the
/// same id.
///
/// Distinct from <see cref="MachineIdProvider"/>, which tags memories with a
/// separately-generated id stored inside the profile config (<c>config.json</c>'s
/// <c>machine_id</c>); the two are not reconciled. This one is a standalone file so reading/
/// writing it can never race a concurrent <c>ProfileConfig</c> save.
/// </summary>
public sealed class MachineId(ConfigRoot config) {
    string MachinePath { get; } = config.Path("machine.json");

    /// <summary>
    /// Returns this machine's stable id, generating and persisting one on first call. Later
    /// calls — in this process or a new one — read the same persisted value back.
    /// </summary>
    public string Get() => ReadPersisted() ?? Create();

    /// <summary>Whether machine.json exists, readable or not — tells a machine that has never had an id
    /// apart from one whose id is momentarily unreadable.</summary>
    public bool IsPersisted => File.Exists(MachinePath);

    /// <summary>
    /// Reads the persisted id straight off disk — what a fresh process (or a fresh call after a
    /// peer process wrote it) would see. Returns null if machine.json doesn't exist yet or is
    /// corrupt (never resurrects a partial/garbled write; the next <see cref="Get"/> just
    /// regenerates).
    /// </summary>
    public string? ReadPersisted() {
        if (!File.Exists(MachinePath)) return null;

        try {
            var json = File.ReadAllText(MachinePath);
            var file = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.MachineIdFile);
            return string.IsNullOrWhiteSpace(file.Id) ? null : file.Id;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // Never throw out of Get(): a corrupt file or a transient read error is "no readable id".
            return null;
        }
    }

    // machine.json only ever appears whole: the id is written to a temp file beside it and moved into
    // place. The first-run move refuses to replace an existing file, so of two processes racing on a
    // new machine exactly one publishes and the other reads the winner's complete id back.
    string Create() {
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);

        if (TryPublish(id, overwrite: false)) return id;

        if (ReadPersisted() is { } peer) return peer;

        // Present but unreadable (corrupt): replace it so Get() settles on one persisted id instead of
        // a fresh GUID per call. Two healers race harmlessly; both return whichever landed.
        TryPublish(id, overwrite: true);

        return ReadPersisted() ?? id;
    }

    bool TryPublish(string id, bool overwrite) {
        var temp = Path.Combine(Path.GetDirectoryName(MachinePath)!, $".machine.json.{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new MachineIdFile(id), CapacitorJsonContext.Default.MachineIdFile));
                stream.Flush(flushToDisk: true);
            }

            if (!overwrite) return ExclusivePublish.TryMove(temp, MachinePath);

            File.Move(temp, MachinePath, overwrite: true);

            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Windows refuses to replace a file a reader holds open; the caller re-reads either way.
            return false;
        } finally {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>On-disk shape of <c>machine.json</c> (see <see cref="MachineId"/>).</summary>
public readonly record struct MachineIdFile(string Id);
