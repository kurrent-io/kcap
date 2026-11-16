using System.Globalization;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Appends what fetching and staging a bundle did to <c>logs/install.log</c> under the root: where the manifest
/// came from, its signature check, the archive and its SHA-256, and any failure with its code. The progress events
/// carrying the same facts are gone once the caller exits, and a rolled-back setup keeps <c>logs/</c>.
///
/// <para>Best effort: an install never fails because its log cannot be written. Lines written before the root exists
/// are held until a later line finds it, so a fetch that fails before creating the root leaves nothing behind.</para></summary>
sealed class LocalInstallLog(LocalRoot root, TimeProvider? time = null) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    readonly List<string> _pending = [];

    public void Write(string message) {
        var at = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        _pending.Add($"{at} {message}");

        if (!Directory.Exists(root.Directory)) return;

        try {
            DurableDirectory.Ensure(LocalLogFiles.Directory(root), root.Directory, OwnerOnly);
            File.AppendAllLines(LocalLogFiles.Install(root), _pending);
            _pending.Clear();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
