using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core;

/// <summary>
/// Which daemon records a source on this machine, so that no source has two recorders or none. One file per key,
/// beside the watcher pid files and never one of them: <c>KillWatcher</c> signals whatever a pid file names, and that
/// must never be a daemon. No claim plus a live pid means a watcher holds the source.
///
/// <para>The file's modification time is the heartbeat. A beat stamps the file already there, so it can't tear the
/// content or bring back a claim a hook took while the beat was on its way. It is wall-clock time: only that counts a
/// laptop's sleep, and a claim has to come out of a sleep stale.</para>
///
/// <para>A take reads and then writes, so whoever takes holds the per-key spawn lock, as a hook's spawn does. Beats
/// and reads take no lock.</para>
/// </summary>
public sealed class RecorderClaims(string directory, TimeProvider time) {
    const string Hosted   = "hosted";
    const string Terminal = "terminal";

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>How long a claim outlives its last beat, longer than a normal daemon restart takes.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>Who holds <paramref name="key"/>. One stat, so a hook can afford it on every session, and it never
    /// throws. Only the file's age counts, so a corrupt claim is still a claim and goes stale like any other: reading
    /// it as none would start a second recorder beside a live daemon.</summary>
    public RecorderClaimState Read(string key) {
        try {
            var claim = new FileInfo(ClaimFile(key));
            if (!claim.Exists) return RecorderClaimState.None;

            // Either way round: a clock set back must not keep a dead daemon's claim fresh until it catches up.
            return (time.GetUtcNow() - claim.LastWriteTimeUtc).Duration() > Grace ? RecorderClaimState.DaemonStale : RecorderClaimState.DaemonFresh;
        } catch {
            return RecorderClaimState.None;
        }
    }

    /// <summary>Takes <paramref name="key"/> for a daemon: a source nobody claims, a stale claim, or the daemon's own
    /// claim again after a restart. False when a fresh claim may be someone else's, or the claim can't be written.</summary>
    public bool TryTake(string key, string daemonName, bool isHosted) {
        if (Read(key) is RecorderClaimState.DaemonFresh && Contents(key)?.Holder != daemonName) return false;

        try {
            Directory.CreateDirectory(directory);
            AtomicFile.Replace(ClaimFile(key), $"{(isHosted ? Hosted : Terminal)}\n{daemonName}", OwnerOnly, followLink: false, durable: true);
            Beat(key);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Marks the claim alive. A claim that has gone stays gone, and its recorder's next check finds that out.</summary>
    public void Beat(string key) {
        try {
            File.SetLastWriteTimeUtc(ClaimFile(key), time.GetUtcNow().UtcDateTime);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            // Nothing to mark.
        }
    }

    /// <summary>Gives up <paramref name="key"/>. With no claim left the source is free, and the recorder that held it
    /// stops at its next tick, so this is also how a hook takes a stale claim.</summary>
    public void Release(string key) {
        try {
            File.Delete(ClaimFile(key));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind, it goes stale and the next hook takes it.
        }
    }

    /// <summary>Gives up <paramref name="key"/> if <paramref name="holder"/> holds it. Any other claim, a corrupt one
    /// too, may be someone's who is recording. It reads and then deletes, so the caller holds the per-key spawn lock, as
    /// a take does.</summary>
    public void ReleaseIfHeldBy(string key, string holder) {
        if (Contents(key)?.Holder == holder) Release(key);
    }

    /// <summary>Whether the claim says its session is hosted. A claim that can't be read says it isn't: a hook from a
    /// hosted agent leaves a stale hosted claim for the daemon's catch-up, which a corrupt claim gives no reason to expect,
    /// and the session would go unrecorded if it never comes. A hook taking a hosted source costs less: it stays on a
    /// watcher, unstamped for the rest of its life but recorded.</summary>
    public bool IsHosted(string key) => Contents(key)?.IsHosted == true;

    /// <summary>The daemon the claim names, or null for a claim that can't be read.</summary>
    public string? Holder(string key) => Contents(key)?.Holder;

    /// <summary>The keys claimed for a session: its own, and each of its subagents', whose files name the session first.</summary>
    /// <returns>Null when the directory can't be read, which says nothing about whether a claim exists.</returns>
    public IReadOnlyList<string>? KeysOf(string sessionId) {
        try {
            return [
                ..Directory.GetFiles(directory, $"{sessionId}.claim").Concat(Directory.GetFiles(directory, $"{sessionId}-*.claim"))
                    .Select(file => Path.GetFileNameWithoutExtension(file))
            ];
        } catch (DirectoryNotFoundException) {
            return [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    internal string ClaimFile(string key) => Path.Combine(directory, $"{key}.claim");

    (bool IsHosted, string Holder)? Contents(string key) {
        try {
            using var stream = new FileStream(ClaimFile(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var text  = reader.ReadToEnd();
            var split = text.IndexOf('\n');
            if (split < 0 || split == text.Length - 1) return null;

            return text[..split] switch {
                Hosted   => (true, text[(split + 1)..]),
                Terminal => (false, text[(split + 1)..]),
                _        => null,
            };
        } catch {
            return null;
        }
    }
}
