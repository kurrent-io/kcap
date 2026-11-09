using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Setup;

/// The `kcap` links made outside <see cref="KcapLinkFolders.For"/>, recorded before they are made so
/// a bundle move and every uninstall can still find them while a batch is pending.
public sealed class AdoptedLinkInventory(ConfigRoot config) {
    const string FileName = "adopted-kcap-links";

    public IReadOnlyList<string> Links() {
        using (config.AcquireLock(FileName)) return Read();
    }

    public void Add(string linkPath) => Update(links => links.Contains(linkPath, StringComparer.Ordinal) ? null : [.. links, linkPath]);

    public void Remove(string linkPath) => Update(links => links.Contains(linkPath, StringComparer.Ordinal) ? [.. links.Where(l => l != linkPath)] : null);

    void Update(Func<List<string>, IEnumerable<string>?> change) {
        using var _ = config.AcquireLock(FileName);

        if (change(Read()) is not { } updated) return;

        System.IO.Directory.CreateDirectory(config.Directory);
        AtomicFile.Replace(
            config.Path(FileName),
            string.Concat(updated.Select(l => l + "\n")),
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite,
            followLink: false,
            durable: true
        );
    }

    List<string> Read() {
        try {
            return [.. File.ReadAllLines(config.Path(FileName)).Where(l => Path.IsPathRooted(l))];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }
}
