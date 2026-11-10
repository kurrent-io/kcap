using System.Runtime.InteropServices;

namespace Capacitor.App.Services.CliAdoption;

internal static partial class WritableFolder {
    const int W_OK = 2;

    /// <summary>Whether this user can add and remove entries in <paramref name="folder"/>.</summary>
    public static bool Check(string folder) => !OperatingSystem.IsWindows() && Directory.Exists(folder) && access(folder, W_OK) == 0;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);
}
