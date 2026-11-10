using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

/// <summary>Moves a finished file into place only if nothing is there yet, atomically: of several
/// concurrent callers exactly one wins. <see cref="File.Move(string, string, bool)"/> without overwrite
/// is not that on Unix, where .NET checks for the destination and then renames over it.</summary>
static partial class ExclusivePublish {
    const int EExist = 17;

    /// <summary>True when <paramref name="source"/> now sits at <paramref name="destination"/>; false
    /// when the destination already existed. <paramref name="source"/> is gone either way on success,
    /// and left for the caller to delete on false.</summary>
    public static bool TryMove(string source, string destination) {
        if (!OperatingSystem.IsWindows()) {
            if (Link(source, destination) == 0) {
                File.Delete(source);

                return true;
            }

            if (Marshal.GetLastPInvokeError() == EExist) return false;
            // A filesystem without hard links falls through to the best the runtime offers.
        }

        try {
            // MoveFileEx without MOVEFILE_REPLACE_EXISTING on Windows: atomic and refuses to replace.
            File.Move(source, destination, overwrite: false);

            return true;
        } catch (IOException) when (File.Exists(destination)) {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existing, string created);
}
