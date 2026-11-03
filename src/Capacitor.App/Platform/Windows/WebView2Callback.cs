using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Windows;

/// A handler object WebView2 can hold: IUnknown plus one Invoke, forwarding to a managed delegate.
/// It answers QueryInterface for each handler interface it is given, all of which must share one
/// Invoke signature. It starts with one reference, which the caller hands to WebView2 and then
/// releases; the last Release frees it and releases the handlers it owns. Nothing it runs may throw
/// into native code, where an exception ends the process.
sealed unsafe class WebView2Callback {
    const int Ok = 0;
    const int NoInterface = unchecked((int)0x80004002);
    const int NullPointer = unchecked((int)0x80004003);

    static readonly nint EventVtable = Vtable((nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&InvokeEvent);
    static readonly nint CompletionVtable = Vtable((nint)(delegate* unmanaged[Stdcall]<nint, int, char*, int>)&InvokeCompletion);

    readonly Guid[] _iids;
    readonly Action<nint, nint>? _onEvent;
    readonly Action<int, string?>? _onCompletion;
    readonly nint[] _owned;
    GCHandle _handle;
    nint _native;
    int _references = 1;

    WebView2Callback(Guid[] iids, Action<nint, nint>? onEvent, Action<int, string?>? onCompletion, nint[] owned) {
        _iids = iids;
        _onEvent = onEvent;
        _onCompletion = onCompletion;
        _owned = owned;
    }

    /// An event handler, Invoke(sender, args). Each of owned is released with it.
    public static nint ForEvent(Guid[] iids, Action<nint, nint> onEvent, params nint[] owned) =>
        Create(new WebView2Callback(iids, onEvent, null, owned), EventVtable);

    /// A completion handler, Invoke(HRESULT errorCode, LPCWSTR result).
    public static nint ForCompletion(Guid iid, Action<int, string?> onCompletion) =>
        Create(new WebView2Callback([iid], null, onCompletion, []), CompletionVtable);

    static nint Create(WebView2Callback callback, nint vtable) {
        callback._handle = GCHandle.Alloc(callback);
        var native = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint));
        native[0] = vtable;
        native[1] = GCHandle.ToIntPtr(callback._handle);
        callback._native = (nint)native;
        return (nint)native;
    }

    static nint Vtable(nint invoke) {
        var table = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        table[WebView2Abi.QueryInterface] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        table[WebView2Abi.AddRef] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[WebView2Abi.Release] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        table[3] = invoke;
        return (nint)table;
    }

    static WebView2Callback? From(nint self) =>
        self == 0 ? null : GCHandle.FromIntPtr(((nint*)self)[1]).Target as WebView2Callback;

    void Free() {
        NativeMemory.Free((void*)_native);
        _handle.Free();
        foreach (var owned in _owned) WebView2Com.Release(owned);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int QueryInterface(nint self, Guid* iid, nint* result) {
        if (result == null) return NullPointer;
        *result = 0;
        if (From(self) is not { } callback || iid == null || (*iid != WebView2Abi.IUnknown && Array.IndexOf(callback._iids, *iid) < 0)) return NoInterface;
        Interlocked.Increment(ref callback._references);
        *result = self;
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint AddRef(nint self) => From(self) is { } callback ? (uint)Interlocked.Increment(ref callback._references) : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint Release(nint self) {
        if (From(self) is not { } callback) return 0;
        var left = Interlocked.Decrement(ref callback._references);
        if (left == 0) callback.Free();
        return (uint)left;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int InvokeEvent(nint self, nint sender, nint args) {
        try {
            From(self)?._onEvent?.Invoke(sender, args);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page web view handler failed: {ex.Message}");
        }
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int InvokeCompletion(nint self, int errorCode, char* result) {
        try {
            From(self)?._onCompletion?.Invoke(errorCode, result == null ? null : new string(result));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page web view handler failed: {ex.Message}");
        }
        return Ok;
    }
}
