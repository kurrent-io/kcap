namespace Capacitor.Cli.Core.Workspace;

static class WorkspaceFiles {
    /// <summary>Writes through a temp file and a rename, so a reader never sees a torn file. A temp left
    /// by a killed write ends in <c>.tmp</c>, which no reader enumerates.</summary>
    public static bool WriteAtomic(string path, string content) {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            try { File.Delete(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    public static void TryDelete(string path) {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
