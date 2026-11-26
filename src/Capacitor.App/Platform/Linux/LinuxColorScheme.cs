using Tmds.DBus.Protocol;

namespace Capacitor.App.Platform.Linux;

/// The desktop's light or dark preference, read once from the XDG settings portal before the first
/// window. Avalonia asks the same portal without waiting and reports Light until the answer lands,
/// so a dark desktop would open in Light and switch to Dark a moment later.
static class LinuxColorScheme {
    const string Service = "org.freedesktop.portal.Desktop";
    const string Path = "/org/freedesktop/portal/desktop";
    const uint PrefersDarkValue = 1; // 0 is no preference and 2 is light, both of which Avalonia reads as Light

    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(250);

    /// Null when the portal is missing, fails, or does not answer in time.
    public static bool? PrefersDark() {
        try {
            // Off the UI thread, so no continuation can wait on the dispatcher this call blocks.
            var read = Task.Run(ReadAsync);
            return read.Wait(Wait) ? read.Result : null;
        } catch (Exception) {
            return null;
        }
    }

    static async Task<bool?> ReadAsync() {
        var connection = DBusConnection.Session;
        var writer = connection.GetMessageWriter();
        // Read rather than ReadOne: every portal version has it. It may wrap the value in an extra variant.
        writer.WriteMethodCallHeader(Service, Path, "org.freedesktop.portal.Settings", "Read", "ss");
        writer.WriteString("org.freedesktop.appearance");
        writer.WriteString("color-scheme");
        var value = await connection.CallMethodAsync(writer.CreateMessage(), (m, _) => m.GetBodyReader().ReadVariantValue());
        while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
        return value.Type == VariantValueType.UInt32 ? value.GetUInt32() == PrefersDarkValue : null;
    }
}
