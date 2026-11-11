using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>
/// Covers the OpenCode plugin installer: kcap.ts write/remove + the
/// version-marker helpers. OpenCode has no hooks.json — kcap ships a dependency-free
/// TypeScript plugin OpenCode auto-loads from <c>~/.config/opencode/plugins/</c>.
/// </summary>
public class OpenCodeExtensionInstallerTests {
    [Test]
    public async Task install_writes_plugin_and_marker() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("plugins", "kcap.ts");

        var ok = OpenCodeExtensionInstaller.Install(path);
        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();

        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content.Contains("export const KcapPlugin")).IsTrue();
        await Assert.That(content.Contains("session.created")).IsTrue();
        await Assert.That(content.Contains("session.idle")).IsTrue();
        await Assert.That(content.Contains("--opencode")).IsTrue();
        // Dependency-free: only node: builtins, no fetched npm package.
        await Assert.That(content.Contains("@opencode-ai")).IsFalse();

        await Assert.That(OpenCodeExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(OpenCodeExtensionInstaller.ReadMarker(path)).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task is_installed_true_when_only_marker_present() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("plugins", "kcap.ts");
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        // Marker present but no kcap.ts (user deleted the file, kept the dir).
        await File.WriteAllTextAsync(Path.Combine(dir, OpenCodeExtensionInstaller.MarkerFileName), "9.9.9");

        await Assert.That(OpenCodeExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task remove_deletes_plugin_and_marker() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("plugins", "kcap.ts");
        OpenCodeExtensionInstaller.Install(path);

        var removed = OpenCodeExtensionInstaller.Remove(path);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(OpenCodeExtensionInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task remove_returns_false_when_absent() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("plugins", "kcap.ts");

        await Assert.That(OpenCodeExtensionInstaller.Remove(path)).IsFalse();
    }

    /// <summary>A re-run of setup must not rewrite a current file, or it reports a change that never happened.</summary>
    [Test]
    public async Task apply_leaves_a_current_file_untouched_and_reports_unchanged() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("plugins", "kcap.ts");
        await Assert.That(OpenCodeExtensionInstaller.Apply(path)).IsEqualTo(SettingsEdit.Changed);

        var before = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, before);

        await Assert.That(OpenCodeExtensionInstaller.Apply(path)).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
    }

    [Test]
    public async Task apply_rewrites_an_edited_file_or_a_stale_marker() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("plugins", "kcap.ts");
        OpenCodeExtensionInstaller.Apply(path);

        await File.WriteAllTextAsync(path, "// edited");
        await Assert.That(OpenCodeExtensionInstaller.Apply(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(OpenCodeExtensionInstaller.ExtensionContent);

        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, OpenCodeExtensionInstaller.MarkerFileName), "0.0.1");
        await Assert.That(OpenCodeExtensionInstaller.Apply(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(OpenCodeExtensionInstaller.ReadMarker(path)).IsEqualTo(CapacitorVersion.Current());
    }
}
