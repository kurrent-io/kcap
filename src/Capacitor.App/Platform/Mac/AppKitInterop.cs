using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Mac;

internal static partial class AppKitInterop {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal static nint String(string value) => Send(GetClass("NSString"), Selector("stringWithUTF8String:"), value);

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Send(nint receiver, nint selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint argument);
}
