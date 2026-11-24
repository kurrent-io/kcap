using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Linux;

internal static partial class GnomeScrollbarPreference {
    const string Gio = "libgio-2.0.so.0";

    internal static bool? ReadOverlayScrolling() {
        var source = GetDefaultSource();
        if (source == 0) return null;
        var schema = Lookup(source, "org.gnome.desktop.interface", 1);
        if (schema == 0) return null;
        try {
            const string key = "overlay-scrolling";
            if (HasKey(schema, key) == 0) return null;
            var settings = NewSettings(schema, 0, 0);
            if (settings == 0) return null;
            try { return ReadBoolean(settings, key) != 0; }
            finally { ReleaseObject(settings); }
        } finally { ReleaseSchema(schema); }
    }

    [LibraryImport(Gio, EntryPoint = "g_settings_schema_source_get_default")]
    private static partial nint GetDefaultSource();

    [LibraryImport(Gio, EntryPoint = "g_settings_schema_source_lookup", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Lookup(nint source, string id, int recursive);

    [LibraryImport(Gio, EntryPoint = "g_settings_schema_has_key", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int HasKey(nint schema, string key);

    [LibraryImport(Gio, EntryPoint = "g_settings_new_full")]
    private static partial nint NewSettings(nint schema, nint backend, nint path);

    [LibraryImport(Gio, EntryPoint = "g_settings_get_boolean", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ReadBoolean(nint settings, string key);

    [LibraryImport(Gio, EntryPoint = "g_settings_schema_unref")]
    private static partial void ReleaseSchema(nint schema);

    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static partial void ReleaseObject(nint value);
}
