namespace Capacitor.Cli.Daemon.Services;

/// <summary>Folds the Windows spellings of a drive or share path into the ordinary one: <c>/</c> into
/// <c>\</c>, and the <c>\\?\</c> and <c>\\.\</c> prefixes (with their <c>UNC\</c> forms) away. Pure string
/// work, so it reads the same on every platform.
///
/// <para>A prefix is removed only where the plain spelling names the same object. Under <c>\\?\</c> a
/// component ending in a dot or a space, a <c>.</c> or <c>..</c>, or a device name such as <c>CON</c> is
/// taken literally, while the plain spelling would trim or reinterpret it; such a path keeps its prefix,
/// and with it never matches an ordinary entry.</para></summary>
public static class WindowsPathSpelling {
    public static string Fold(string path) {
        var p = path.Replace('/', '\\');

        if (StripPrefix(p, @"\\?\UNC\") is { } uncExtended) return FoldIfPlain(p, @"\\" + uncExtended, uncExtended);
        if (StripPrefix(p, @"\\.\UNC\") is { } uncDevice) return FoldIfPlain(p, @"\\" + uncDevice, uncDevice);
        if (StripPrefix(p, @"\\?\") is { } extended && IsDriveRooted(extended)) return FoldIfPlain(p, extended, extended[2..]);
        if (StripPrefix(p, @"\\.\") is { } device && IsDriveRooted(device)) return FoldIfPlain(p, device, device[2..]);

        return p;
    }

    static string? StripPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;

    static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    static string FoldIfPlain(string original, string folded, string components) =>
        components.Split('\\', StringSplitOptions.RemoveEmptyEntries).All(IsPlainComponent) ? folded : original;

    static bool IsPlainComponent(string component) =>
        component is not ("." or "..")
     && !component.EndsWith('.') && !component.EndsWith(' ')
     && !IsDeviceName(component);

    static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

    // A device name is reserved with any extension too: `NUL.txt` is the null device.
    static bool IsDeviceName(string component) {
        var stem = component.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return true;

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
