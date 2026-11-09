using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

internal static class OtherCliInstallReader {
    internal const string NpmPackage = "@kurrent/kcap";

    const string PlatformPackage = "kcap-darwin-arm64";

    /// An npm or script install the binary belongs to, or null for anything else, an install whose daemon binary or
    /// version cannot be read included: that one is left alone rather than guessed at.
    internal static async Task<OtherCliInstall?> FromBinaryAsync(string binaryPath, EntryIdentity identity, CancellationToken ct) {
        var resolved = CanonicalPath.Resolve(binaryPath);
        var found    = await Task.Run(() => ReadNpm(resolved) ?? ReadScript(resolved), ct).ConfigureAwait(false);
        if (found is null) return null;

        return await identity.OfAsync(found.DaemonBinary, ct).ConfigureAwait(false) is { } id ? found with { Identity = id } : null;
    }

    /// The global prefix of the npm install <paramref name="entry"/> runs, read from where its package lives; null when
    /// npm does not own it.
    internal static string? NpmPrefixOf(string entry) {
        if (OperatingSystem.IsWindows()) return WindowsNpmPrefix(entry);

        var package = CanonicalPath.Resolve(entry);
        var at      = package.IndexOf($"/lib/node_modules/{NpmPackage}/", StringComparison.Ordinal);

        return at > 0 ? package[..at] : null;
    }

    /// npm on Windows writes a global package's <c>kcap.cmd</c> into the prefix itself, beside its
    /// <c>node_modules</c>; anything else there, a <c>kcap.exe</c> included, is not one npm would replace.
    static string? WindowsNpmPrefix(string entry) {
        if (!string.Equals(Path.GetExtension(entry), ".cmd", StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = Path.GetDirectoryName(entry);

        return prefix is not null && File.Exists(Path.Combine(prefix, "node_modules", "@kurrent", "kcap", "package.json")) ? prefix : null;
    }

    // The launcher's own bin/kcap.js is what npm links onto PATH, so it stands for the platform binary beside or
    // inside its package.
    static OtherCliInstall? ReadNpm(string resolved) {
        string launcher;
        string platformBin;

        if (Path.GetFileName(resolved) == "kcap.js" && LauncherOf(resolved) is { } own) {
            launcher = own;
            var beside = Path.Combine(Path.GetDirectoryName(launcher)!, PlatformPackage, "bin");
            var inside = Path.Combine(launcher, "node_modules", "@kurrent", PlatformPackage, "bin");
            if (File.Exists(Path.Combine(beside, "kcap"))) platformBin = beside;
            else if (File.Exists(Path.Combine(inside, "kcap"))) platformBin = inside;
            else return null;
        } else if (NpmInstallLayout.IsPlatformBinary(resolved, File.Exists)) {
            platformBin = Path.GetDirectoryName(resolved)!;
            var sibling = Path.GetFullPath(Path.Combine(platformBin, "..", "..", "kcap"));
            launcher = File.Exists(Path.Combine(sibling, "bin", "kcap.js"))
                ? sibling
                : Path.GetFullPath(Path.Combine(platformBin, "..", "..", "..", ".."));
        } else {
            return null;
        }

        if (VersionOf(launcher) is not { } version) return null;

        var owned = new List<string> { launcher };
        var package = Path.GetDirectoryName(platformBin)!;
        if (!CanonicalPath.IsWithin(package, launcher) && Directory.Exists(package)) owned.Add(package);

        return new OtherCliInstall(OtherCliInstallKind.Npm, launcher, version, Path.Combine(platformBin, "kcap"),
            Path.Combine(platformBin, "kcap-daemon"), "", owned);
    }

    static string? LauncherOf(string launcherScript) {
        var bin = Path.GetDirectoryName(launcherScript);
        if (Path.GetFileName(bin) != "bin") return null;

        var package = Path.GetDirectoryName(bin);

        return package is not null && Path.GetFileName(package) == "kcap" && Path.GetFileName(Path.GetDirectoryName(package)) == "@kurrent"
            ? package
            : null;
    }

    static string? VersionOf(string launcher) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(launcher, "package.json")));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(v.GetString())
                    ? v.GetString()
                    : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    static OtherCliInstall? ReadScript(string resolved) {
        if (ScriptInstallLayout.FromBinary(resolved, File.Exists) is not { } layout) return null;

        var active  = CanonicalPath.Resolve(layout.Current);
        var version = Path.GetDirectoryName(active) is { } parent && PathComparison.Equal(parent, layout.Versions)
            ? Path.GetFileName(active)
            : null;
        if (string.IsNullOrEmpty(version)) return null;

        var bin = Path.Combine(active, "bin");

        return new OtherCliInstall(OtherCliInstallKind.Script, layout.Root, version, Path.Combine(bin, "kcap"),
            Path.Combine(bin, "kcap-daemon"), "", [layout.Versions, layout.Current, layout.Marker]);
    }
}
