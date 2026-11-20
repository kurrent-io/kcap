using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Capacitor.App.Platform.Mac.AppKitInterop;

namespace Capacitor.App.Platform.Mac;

[SupportedOSPlatform("macos")]
internal static class MacWindowPreferences {
    internal static string? ReadTitleBarDoubleClickAction() {
        var defaults = Send(GetClass("NSUserDefaults"), Selector("standardUserDefaults"));
        var action = Send(defaults, Selector("stringForKey:"), String("AppleActionOnDoubleClick"));
        return Marshal.PtrToStringUTF8(Send(action, Selector("UTF8String")));
    }
}
