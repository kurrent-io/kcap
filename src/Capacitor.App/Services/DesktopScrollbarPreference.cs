using System.Security;
using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;
using Microsoft.Win32;

namespace Capacitor.App.Services;

internal static class DesktopScrollbarPreference {
    internal const string ResourceKey = "KcapTranscriptAutoHideScrollbar";

    static DesktopScrollbarPreference() {
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>((window, _) => {
            if (window.IsActive && Application.Current?.Resources is { } resources && resources.ContainsKey(ResourceKey))
                Apply(resources, true);
        });
    }

    internal static void Apply(IResourceDictionary resources, bool preview) {
        if (!preview) {
            resources.Remove(ResourceKey);
            return;
        }
        var autoHide = ReadAutoHide() ?? true;
        if (!resources.TryGetValue(ResourceKey, out var current) || !Equals(current, autoHide))
            resources[ResourceKey] = autoHide;
    }

    static bool? ReadAutoHide() {
        try {
            if (OperatingSystem.IsMacOS()) {
                var scroller = AppKitInterop.GetClass("NSScroller");
                return scroller == 0 ? null : AppKitInterop.Send(scroller, AppKitInterop.Selector("preferredScrollerStyle")) != 0;
            }
            if (OperatingSystem.IsWindows()) {
                using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility");
                return settings?.GetValue("DynamicScrollbars") is int value ? value != 0 : null;
            }
            if (OperatingSystem.IsLinux()
                && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("GNOME", StringComparison.OrdinalIgnoreCase) == true)
                return GnomeScrollbarPreference.ReadOverlayScrolling();
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException
            or SecurityException or UnauthorizedAccessException) {
            return null;
        }
        return null;
    }
}
