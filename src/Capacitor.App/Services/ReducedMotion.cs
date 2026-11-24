using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Services;

public static partial class ReducedMotion {
    static bool _installed;
    static bool? _override;
    static DesktopMotion? _previewMotion;
    static CancellationToken _lifetime;
    static Task? _portalWatch;
    static bool? _portalPreference;

    public static bool IsOn { get; private set; }
    internal static bool? SystemPreference { get; private set; }

    public static event Action? Changed;
    internal static event Action? SystemPreferenceChanged;

    internal static void SetPreviewMotion(DesktopMotion? choice) {
        _previewMotion = choice;
        Refresh();
    }

    public static void Install(CancellationToken lifetime) {
        if (_installed) return;
        _lifetime = lifetime;
        _installed = true;
        Refresh();
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>((window, _) => {
            if (window.IsActive) Refresh();
        });
    }

    public static void Refresh() {
        if (_lifetime.IsCancellationRequested) return;
        if (OperatingSystem.IsLinux() && _installed && _portalWatch?.IsCompleted != false) {
            var cancellation = _lifetime;
            _portalWatch = Task.Run(() => PortalReducedMotion.WatchAsync(value => Dispatcher.UIThread.Post(() => {
                if (cancellation.IsCancellationRequested) return;
                _portalPreference = value;
                Apply(_override ?? value);
            }), cancellation), cancellation);
        }
        Apply(_override ?? (OperatingSystem.IsLinux() ? _portalPreference : Read()));
    }

    static void Apply(bool? preference) {
        if (SystemPreference != preference) {
            SystemPreference = preference;
            SystemPreferenceChanged?.Invoke();
        }
        var on = _previewMotion == DesktopMotion.Reduce || preference == true;
        if (on != IsOn) {
            IsOn = on;
            Changed?.Invoke();
        }
    }

    internal static void OverrideForTesting(bool? on) {
        _override = on;
        Refresh();
    }

    static bool? Read() {
        try {
            if (OperatingSystem.IsMacOS()) {
                var workspace = AppKitInterop.Send(AppKitInterop.GetClass("NSWorkspace"), AppKitInterop.Selector("sharedWorkspace"));
                return (AppKitInterop.Send(workspace, AppKitInterop.Selector("accessibilityDisplayShouldReduceMotion")) & 0xFF) != 0;
            }
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(GetClientAreaAnimation, 0, out var animations, 0) ? animations == 0 : null;
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
            return null;
        }
        return null;
    }

    const uint GetClientAreaAnimation = 0x1042;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
