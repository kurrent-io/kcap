using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Replaces a file the way rename(2) does: whoever has the old one open keeps reading it.
/// <see cref="File.Move(string, string, bool)"/> gets ACCESS_DENIED while any handle is open on the target, even
/// one sharing delete, so a reader that takes no lock would fail the writer.</summary>
[SupportedOSPlatform("windows")]
static unsafe partial class PosixRename {
    const uint Delete           = 0x00010000;
    const uint ShareAll         = 7;
    const uint OpenExisting     = 3;
    const uint OpenReparsePoint = 0x00200000;
    const int  FileRenameInfoEx = 22;
    const uint ReplaceIfExists  = 1;
    const uint PosixSemantics   = 2;

    /// <returns>False, with nothing moved, on a volume or Windows build without it, or over a handle that does not
    /// share delete.</returns>
    public static bool TryReplace(string source, string destination) {
        // A link swapped in at the source must move as itself, as it would by name, not move its target.
        using var file = CreateFile(source, Delete, ShareAll, 0, OpenExisting, OpenReparsePoint, 0);
        if (file.IsInvalid) return false;

        var name = Path.GetFullPath(destination);
        var size = sizeof(RenameInfo) + name.Length * sizeof(char);

        fixed (byte* buffer = new byte[size]) {
            var info = (RenameInfo*)buffer;
            info->Flags          = ReplaceIfExists | PosixSemantics;
            info->FileNameLength = (uint)(name.Length * sizeof(char));
            name.CopyTo(new Span<char>(&info->FileName, name.Length));

            return SetFileInformationByHandle(file, FileRenameInfoEx, info, (uint)size);
        }
    }

    /// <summary>FILE_RENAME_INFO; the name runs on past the end of the struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct RenameInfo {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);
}
