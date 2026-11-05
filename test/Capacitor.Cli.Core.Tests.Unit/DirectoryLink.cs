namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>A directory link any account can make: a junction on Windows, where a symlink needs Developer Mode, and a
/// symlink elsewhere.</summary>
static class DirectoryLink {
    public static void Create(string path, string target) {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(path, target);
        else Directory.CreateSymbolicLink(path, target);
    }
}
