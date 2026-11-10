using System.Text.Json;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The per-user list of vendor accounts. Beside the daemons directory and, like it, blind
/// to <c>KCAP_CONFIG_DIR</c>: two config roots on one machine must see one list, or each would wire
/// the same vendor settings file and undo the other.</summary>
public sealed class AccountStore {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir  = OwnerOnlyFile | UnixFileMode.UserExecute;

    public AccountStore(string directory) {
        Directory = directory;
        Readings  = new ReadingStore(Path.Combine(directory, "readings"), Path.Combine(directory, "readings.lock"), TryLoad, EnsureDirectory);
    }

    public string Directory { get; }

    public ReadingStore Readings { get; }

    public string RegistryPath => Path.Combine(Directory, "accounts.json");
    string LockPath     => Path.Combine(Directory, "accounts.lock");
    string HostPath     => Path.Combine(Directory, "host.json");

    public static AccountStore Beside(DaemonStore daemons) =>
        new(Path.Combine(Path.GetDirectoryName(daemons.Directory)!, "accounts"));

    /// <summary>Not re-entrant: never call <see cref="HostId"/>, <see cref="Mutate{T}"/> or <see cref="MutateWithReadings{T}"/> while holding it.</summary>
    public IDisposable Lock() => ConfigFileLock.Acquire(LockPath);

    /// <summary>How long a missing registry is looked for again while a save is replacing it. On Windows the
    /// name can be absent for a moment inside the rename, and a lock-free reader that took that for "no
    /// accounts" would send its caller to the default layout. A rename takes well under a millisecond.</summary>
    internal TimeSpan ReplaceWait { get; init; } = TimeSpan.FromMilliseconds(50);

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    static readonly TimeSpan ReplacePoll = TimeSpan.FromMilliseconds(5);

    // A save killed between creating its temporary and the rename leaves it behind for good; past this age a
    // temporary is that leftover, and the registry beside it is missing rather than being replaced.
    static readonly TimeSpan LiveTemporaryAge = TimeSpan.FromSeconds(5);

    public AccountRegistry Load() {
        if (ReadRegistry() is not { } text) return new AccountRegistry();
        AccountRegistry registry;
        try {
            registry = JsonSerializer.Deserialize(text, AccountRegistryJsonContext.Default.AccountRegistry)
                ?? throw new InvalidDataException($"{RegistryPath} is empty.");
        } catch (JsonException ex) {
            throw new InvalidDataException($"{RegistryPath} is not a valid account registry.", ex);
        }

        var accounts = (registry.Accounts ?? []).Where(Usable).ToList();
        return accounts.Count == registry.Accounts?.Count ? registry : registry with { Accounts = accounts };
    }

    /// <summary>Null when there is no registry. Takes no lock: <see cref="MutateHeld{T}"/> reads through here
    /// under <c>accounts.lock</c>, which is not re-entrant.</summary>
    string? ReadRegistry() {
        var started = Time.GetTimestamp();

        while (true) {
            if (TryReadRegistry() is { } text) return text;

            // Sampled between two reads, so a rename that finishes in between is still read.
            var replacing = ReplaceInFlight();
            if (TryReadRegistry() is { } after) return after;
            if (!replacing || Time.GetElapsedTime(started) >= ReplaceWait) return null;

            Thread.Sleep(ReplacePoll);
        }
    }

    // Producers read without accounts.lock; a reader denying writers would fail a concurrent save's rename on Windows.
    string? TryReadRegistry() {
        try {
            return File.Exists(RegistryPath) ? File.ReadAllTextShared(RegistryPath) : null;
        } catch (FileNotFoundException) {
            return null;
        }
    }

    /// <summary>A live save's temporary beside the missing registry. <c>accounts.lock</c> names a mutex, not a
    /// file, so the temporary is the only trace a save leaves on disk.</summary>
    bool ReplaceInFlight() {
        var now = Time.GetUtcNow().UtcDateTime;
        return Temporaries().Any(t => (now - t.LastWriteTimeUtc).Duration() < LiveTemporaryAge);
    }

    IEnumerable<FileInfo> Temporaries() {
        try {
            var (directory, pattern) = AtomicFile.Temporaries(RegistryPath);
            return System.IO.Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles(pattern).ToList() : [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    // Only under accounts.lock, which every save holds: no temporary seen there belongs to a live save.
    void DeleteLeftoverTemporaries() {
        foreach (var temporary in Temporaries()) {
            try { temporary.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // A hand edit can leave an entry no path lookup can use; dropping it keeps every hook working.
    static bool Usable(VendorAccount? account) =>
        account is { Id.Length: > 0, Directory: { } dir } && !string.IsNullOrWhiteSpace(dir) && Path.IsPathRooted(dir) && Normalizable(dir);

    static bool Normalizable(string dir) {
        try {
            AccountDirectory.Normalize(dir);
            return true;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) {
            return false;
        }
    }

    /// <summary>For hooks and watchers, which must never fail on the registry: null sends the caller to
    /// the environment-derived layout.</summary>
    public AccountRegistry? TryLoad() {
        try { return Load(); }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public T Mutate<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) => MutateWithRevision(change).Result;

    /// <summary>Saves only when <c>Next</c> is a different reference from the registry it was given; a change
    /// that keeps the reference writes nothing and reports the current revision.</summary>
    public (T Result, long Revision) MutateWithRevision<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        using var _ = Lock();
        return MutateHeld(change);
    }

    /// <summary><see cref="MutateWithRevision{T}"/> for a caller already holding <see cref="Lock"/>.</summary>
    public (T Result, long Revision) MutateHeld<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        var current = Load();
        var (next, result) = change(current);
        return (result, Commit(current, next).Revision);
    }

    /// <summary>Holds <c>accounts.lock</c> and then <c>readings.lock</c> from the first read to the last write,
    /// so no producer commits between <paramref name="change"/> reading a reading and <paramref name="afterSave"/>
    /// acting on it. <paramref name="change"/> only reads; every reading write or delete belongs in
    /// <paramref name="afterSave"/>, which runs with the registry as persisted and not at all when the save
    /// throws, so a failed save leaves the readings as they were.</summary>
    public (T Result, long Revision) MutateWithReadings<T>(
        Func<AccountRegistry, HeldReadings, (AccountRegistry Next, T Result)> change,
        Action<AccountRegistry, HeldReadings>?                                 afterSave = null
    ) {
        using var accountsLease = Lock();
        using var readingsLease = Readings.Lock();

        var held           = new HeldReadings(Readings);
        var current        = Load();
        var (next, result) = change(current, held);
        var saved          = Commit(current, next);
        afterSave?.Invoke(saved, held);
        return (result, saved.Revision);
    }

    AccountRegistry Commit(AccountRegistry current, AccountRegistry next) {
        if (ReferenceEquals(next, current)) return current;

        var saved = next with { Revision = current.Revision + 1 };
        Save(saved);
        return saved;
    }

    /// <summary>A fresh read for a caller holding <see cref="Lock"/>.</summary>
    public VendorAccount? FindHeld(string id) => Load().Accounts.FirstOrDefault(a => a.Id == id);

    public VendorAccount? Find(HarnessId vendor, string directory) =>
        Load().Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, directory));

    public string HostId() {
        using var _ = Lock();
        if (File.Exists(HostPath)) {
            try {
                if (JsonSerializer.Deserialize(File.ReadAllTextShared(HostPath), AccountRegistryJsonContext.Default.HostIdentity) is { HostId: { Length: > 0 } existing })
                    return existing;
            } catch (JsonException ex) {
                throw new InvalidDataException($"{HostPath} is not a valid host identity.", ex);
            }
            throw new InvalidDataException($"{HostPath} has no host_id.");
        }

        var created = new HostIdentity(Guid.NewGuid().ToString("N"));
        EnsureDirectory();
        AtomicFile.Replace(HostPath, JsonSerializer.Serialize(created, AccountRegistryJsonContext.Default.HostIdentity), OwnerOnlyFile);
        return created.HostId;
    }

    void Save(AccountRegistry registry) {
        EnsureDirectory();
        DeleteLeftoverTemporaries();
        AtomicFile.Replace(RegistryPath, JsonSerializer.Serialize(registry, AccountRegistryJsonContext.Default.AccountRegistry), OwnerOnlyFile);
    }

    void EnsureDirectory() {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
        else {
            System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDir);
            File.SetUnixFileMode(Directory, OwnerOnlyDir);
        }
    }
}
