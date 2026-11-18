using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Spectre.Console;

namespace Capacitor.Cli;

/// <summary>The console's width when no standard handle can report it. .NET on Windows asks stdout, stderr and
/// stdin in turn, and stdin is never a screen buffer, so with both output streams piped Spectre wraps at its
/// 80-column default however wide the console is; on Unix the terminal still answers through stdin. The
/// active screen buffer, <c>CONOUT$</c>, answers whatever the standard handles are.</summary>
[SupportedOSPlatform("windows")]
static partial class WindowsConsoleWidth {
    const uint GenericRead    = 0x80000000;
    const uint GenericWrite   = 0x40000000;
    const uint FileShareRead  = 0x1;
    const uint FileShareWrite = 0x2;
    const uint OpenExisting   = 3;

    /// <summary>Creates Spectre's default console, so only for a command that has no Console.SetOut ahead.</summary>
    public static void FitSpectre() {
        if (StandardHandlesMeasure()) return;

        if (ActiveWindowWidth() is { } width) AnsiConsole.Profile.Width = width;
    }

    /// <summary>The active window's width, read from <c>CONOUT$</c>; null for a console without a window.</summary>
    public static int? Measure() => ActiveWindowWidth();

    static bool StandardHandlesMeasure() {
        try {
            return Console.BufferWidth > 0;
        } catch (IOException) {
            return false;
        }
    }

    /// <summary>Null for a console without a window: a child started with <c>CREATE_NO_WINDOW</c> has a console
    /// nobody reads, and its default width would only change what a parent captures.</summary>
    static unsafe int? ActiveWindowWidth() {
        if (GetConsoleWindow() == 0) return null;

        var handle = CreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle == -1) return null;

        try {
            ScreenBufferInfo info = default;

            if (!GetConsoleScreenBufferInfo(handle, &info)) return null;

            var width = info.WindowRight - info.WindowLeft + 1;

            return width > 0 ? width : null;
        } finally {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ScreenBufferInfo {
        public short  SizeX;
        public short  SizeY;
        public short  CursorX;
        public short  CursorY;
        public ushort Attributes;
        public short  WindowLeft;
        public short  WindowTop;
        public short  WindowRight;
        public short  WindowBottom;
        public short  MaximumX;
        public short  MaximumY;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetConsoleScreenBufferInfo(nint handle, ScreenBufferInfo* info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
