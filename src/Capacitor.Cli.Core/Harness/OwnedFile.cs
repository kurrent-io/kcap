namespace Capacitor.Cli.Core.Harness;

/// <summary>A file kcap writes whole, with a version marker beside it. Rewritten only when the file or its marker
/// differs from what this build writes, so a re-run leaves both alone and reports nothing changed.</summary>
public static class OwnedFile {
    public static SettingsEdit Write(string path, string content, string? marker, Action<string> writeMarker) {
        if (marker == CapacitorVersion.Current() && Holds(path, content)) return SettingsEdit.Unchanged;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            writeMarker(path);

            return SettingsEdit.Changed;
        } catch {
            return SettingsEdit.Failed;
        }
    }

    static bool Holds(string path, string content) {
        try {
            return File.Exists(path) && File.ReadAllText(path) == content;
        } catch {
            return false;
        }
    }
}
