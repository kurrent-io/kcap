using System.Runtime.InteropServices;
using static Capacitor.App.Platform.Mac.AppKitInterop;

namespace Capacitor.App.Platform.Mac;

/// The AppKit menu calls Avalonia does not make. It never hands its menus to the windowsMenu and
/// helpMenu slots, and only a registered Window menu gets Move to a display, the tiling items and
/// the window list, and only a registered Help menu gets the search field.
public static partial class AppKitMenus {
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const nint AssociationRetainNonatomic = 1;

    static nint AdoptedKey => Selector("kcapAdoptedWindowsMenu");

    /// Registers the current main menu's submenus with these titles; one that is missing is skipped.
    public static void Adopt(string windowTitle, string helpTitle) {
        if (!OperatingSystem.IsMacOS()) return;

        var app = SharedApplication();
        var mainMenu = Send(app, Selector("mainMenu"));
        if (mainMenu == 0) return;

        // AppKit adds another block of tiling items to a menu each time it is registered, even
        // with the same menu, so each menu is registered once. AppKit keeps the window list current
        // in every menu it has registered, not only the latest.
        var windowMenu = Submenu(mainMenu, windowTitle);
        if (windowMenu != 0 && GetAssociatedObject(windowMenu, AdoptedKey) == 0) {
            SendVoid(app, Selector("setWindowsMenu:"), windowMenu);
            SetAssociatedObject(windowMenu, AdoptedKey, Send(GetClass("NSNull"), Selector("null")), AssociationRetainNonatomic);
        }

        var helpMenu = Submenu(mainMenu, helpTitle);
        if (helpMenu != 0) SendVoid(app, Selector("setHelpMenu:"), helpMenu);
    }

    /// Turns off the "Enter Full Screen" item AppKit adds to the Window menu, which the app's own
    /// Toggle Full Screen duplicates and which piles up as windows take turns being key. Avalonia
    /// turns it off only for a process outside a bundle, so the installed app needs this.
    /// Registration-domain only: nothing is written to the user's preferences. Must run before
    /// NSApplication starts.
    public static void SuppressFullScreenItem() {
        if (!OperatingSystem.IsMacOS()) return;

        var no = Send(GetClass("NSNumber"), Selector("numberWithBool:"), (nint)0);
        var key = String("NSFullScreenMenuItemEverywhere");
        var defaults = Send(GetClass("NSDictionary"), Selector("dictionaryWithObject:forKey:"), no, key);
        SendVoid(Send(GetClass("NSUserDefaults"), Selector("standardUserDefaults")), Selector("registerDefaults:"), defaults);
    }

    /// Filled from the bundle's Info.plist, so a build run outside a bundle shows no version.
    public static void ShowAboutPanel() {
        if (!OperatingSystem.IsMacOS()) return;

        SendVoid(SharedApplication(), Selector("orderFrontStandardAboutPanel:"), 0);
    }

    static nint SharedApplication() => Send(GetClass("NSApplication"), Selector("sharedApplication"));

    static nint Submenu(nint menu, string title) {
        var item = Send(menu, Selector("itemWithTitle:"), String(title));
        return item == 0 ? 0 : Send(item, Selector("submenu"));
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getAssociatedObject")]
    private static partial nint GetAssociatedObject(nint obj, nint key);

    [LibraryImport(ObjC, EntryPoint = "objc_setAssociatedObject")]
    private static partial void SetAssociatedObject(nint obj, nint key, nint value, nint policy);
}
