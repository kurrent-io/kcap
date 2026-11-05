using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Core;

/// <summary>Expands 8.3 short-name components (<c>C:\PROGRA~1</c>), which no link resolution touches, so
/// a path compares equal to the long spelling of the same directory.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsLongPath {
    /// <summary>False when the path does not exist or the expansion fails; the caller keeps the path as
    /// given.</summary>
    public static unsafe bool TryExpand(string path, out string expanded) {
        expanded = path;
        var buffer = new char[260];

        while (true) {
            uint length;
            fixed (char* longPath = buffer) length = GetLongPathNameW(path, longPath, (uint)buffer.Length);
            if (length == 0) return false;
            if (length < buffer.Length) {
                expanded = new string(buffer, 0, (int)length);
                return true;
            }
            buffer = new char[length];
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathNameW(string shortPath, char* longPath, uint bufferLength);
}
