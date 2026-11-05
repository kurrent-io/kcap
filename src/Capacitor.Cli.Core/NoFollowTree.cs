namespace Capacitor.Cli.Core;

/// <summary>Recursive delete that never follows a symlink or junction out of the tree: a link, at the top or
/// nested, is unlinked and its target left alone. Throws when an entry cannot be removed.</summary>
public static class NoFollowTree {
    public static void Delete(string path) {
        // Attributes do not follow a link, where Directory.Exists does and would read a dangling directory
        // link as a file, which a Windows reparse point refuses to File.Delete.
        var attributes = new FileInfo(path).Attributes;

        var isDirectory = attributes.HasFlag(FileAttributes.Directory);

        if (attributes.HasFlag(FileAttributes.ReparsePoint) && isDirectory) Directory.Delete(path);
        else if (isDirectory)                                               DeleteContentsAndSelf(path);
        else                                                                File.Delete(path);
    }

    static void DeleteContentsAndSelf(string path) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Delete(entry);

        Directory.Delete(path);
    }
}
