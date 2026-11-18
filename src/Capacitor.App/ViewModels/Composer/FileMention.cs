namespace Capacitor.App.ViewModels.Composer;

/// A folder's path ends in "/".
public sealed record FileMention(string Path) {
    int Cut => Path.LastIndexOf('/', Math.Max(Path.Length - 2, 0));

    public string Name => Path[(Cut + 1)..];

    public string Folder => Path[..Math.Max(Cut, 0)];
}
