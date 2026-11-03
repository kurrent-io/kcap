using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Windows;

/// Calls into WebView2's COM objects through their vtables. Classic COM interop is not trim-safe and
/// the package's own interfaces are internal, so each call names its slot from WebView2Abi. A call
/// that fails reports false or zero rather than throwing: it runs where a throw would escape into
/// native code.
static unsafe class WebView2Com {
    static nint Slot(nint self, int slot) => (*(nint**)self)[slot];

    public static nint QueryInterface(nint self, Guid iid) {
        if (self == 0) return 0;
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(self, WebView2Abi.QueryInterface))(self, &iid, &result);
        return hr >= 0 ? result : 0;
    }

    public static void AddRef(nint self) {
        if (self != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, WebView2Abi.AddRef))(self);
    }

    public static void Release(nint self) {
        if (self != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, WebView2Abi.Release))(self);
    }

    /// An [out, retval] interface pointer, which the caller releases.
    public static nint GetObject(nint self, int slot) {
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, slot))(self, &result);
        return hr >= 0 ? result : 0;
    }

    /// An [out, retval] LPWSTR, which WebView2 allocates with CoTaskMemAlloc.
    public static string? GetString(nint self, int slot) {
        nint text = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, slot))(self, &text);
        if (hr < 0 || text == 0) return null;
        try {
            return Marshal.PtrToStringUni(text);
        } finally {
            Marshal.FreeCoTaskMem(text);
        }
    }

    public static bool GetBool(nint self, int slot, out bool value) {
        int result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(self, slot))(self, &result);
        value = result != 0;
        return hr >= 0;
    }

    public static bool PutBool(nint self, int slot, bool value) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(self, slot))(self, value ? 1 : 0) >= 0;

    /// add_X(handler, EventRegistrationToken*). The token is one int64, so it travels as a long.
    public static bool AddHandler(nint self, int slot, nint handler, out long token) {
        long result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(self, slot))(self, handler, &result);
        token = result;
        return hr >= 0;
    }

    public static bool RemoveHandler(nint self, int slot, long token) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(self, slot))(self, token) >= 0;

    public static bool CallWithString(nint self, int slot, string text) {
        fixed (char* chars = text)
            return ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(self, slot))(self, chars) >= 0;
    }

    public static bool CallWithStringAndHandler(nint self, int slot, string text, nint handler) {
        fixed (char* chars = text)
            return ((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Slot(self, slot))(self, chars, handler) >= 0;
    }
}
