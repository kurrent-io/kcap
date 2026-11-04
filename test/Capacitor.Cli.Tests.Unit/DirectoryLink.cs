using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A directory link made the way each OS's installer makes one: a junction on Windows, which needs no
/// privilege, and a symlink elsewhere.</summary>
static class DirectoryLink {
    public static void Create(string path, string target) {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(path, target);
        else Directory.CreateSymbolicLink(path, target);
    }
}
