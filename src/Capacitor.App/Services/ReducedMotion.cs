using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Services;

/// The OS "reduce motion" setting: macOS Accessibility → Display → Reduce motion, or Windows "Show
/// animations in Windows" turned off. Read at start and again whenever an app window is activated,
/// which is where a user comes back from changing it. Linux reads as off.
public static partial class ReducedMotion {
    static bool _installed;
    static bool? _override;

    public static bool IsOn { get; private set; }

    /// Raised on the UI thread when <see cref="IsOn"/> changes.
    public static event Action? Changed;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        Refresh();
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>((window, _) => {
            if (window.IsActive) Refresh();
        });
    }

    public static void Refresh() {
        var on = _override ?? Read();
        if (on == IsOn) return;
        IsOn = on;
        Changed?.Invoke();
    }

    /// Pins the setting for a test, or hands it back to the OS with null.
    internal static void OverrideForTesting(bool? on) {
        _override = on;
        Refresh();
    }

    static bool Read() {
        try {
            if (OperatingSystem.IsMacOS()) {
                var workspace = AppKitInterop.Send(AppKitInterop.GetClass("NSWorkspace"), AppKitInterop.Selector("sharedWorkspace"));
                return (AppKitInterop.Send(workspace, AppKitInterop.Selector("accessibilityDisplayShouldReduceMotion")) & 0xFF) != 0;
            }
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(GetClientAreaAnimation, 0, out var animations, 0) && animations == 0;
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
            return false;
        }
        return false;
    }

    const uint GetClientAreaAnimation = 0x1042;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
