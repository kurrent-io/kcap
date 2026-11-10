using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Capacitor.App.Platform.Mac;

[SupportedOSPlatform("macos")]
internal static partial class MacApplicationDiscovery {
    const string LaunchServices = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/LaunchServices";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const uint Utf8 = 0x08000100;

    public static bool Installed(string bundleIdentifier) {
        var identifier = CFStringCreateWithCString(0, bundleIdentifier, Utf8);
        if (identifier == 0) return false;
        nint applications = 0, error = 0;
        try {
            applications = LSCopyApplicationURLsForBundleIdentifier(identifier, out error);
            if (applications == 0) return false;
            for (nint i = 0; i < CFArrayGetCount(applications); i++) {
                var path = CFURLCopyFileSystemPath(CFArrayGetValueAtIndex(applications, i), 0);
                if (path == 0) continue;
                try {
                    var buffer = new byte[checked((int)CFStringGetMaximumSizeForEncoding(CFStringGetLength(path), Utf8) + 1)];
                    if (CFStringGetCString(path, buffer, buffer.Length, Utf8)
                        && Directory.Exists(Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)))) return true;
                } finally {
                    CFRelease(path);
                }
            }
            return false;
        } finally {
            if (applications != 0) CFRelease(applications);
            if (error != 0) CFRelease(error);
            CFRelease(identifier);
        }
    }

    [LibraryImport(LaunchServices)]
    private static partial nint LSCopyApplicationURLsForBundleIdentifier(nint bundleIdentifier, out nint error);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCopyFileSystemPath(nint url, nint pathStyle);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint value);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFStringGetCString(nint value, [Out] byte[] buffer, nint bufferSize, uint encoding);
    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
