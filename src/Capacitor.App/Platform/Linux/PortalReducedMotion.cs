using Tmds.DBus.Protocol;

namespace Capacitor.App.Platform.Linux;

internal static class PortalReducedMotion {
    const string Destination = "org.freedesktop.portal.Desktop";
    const string Path = "/org/freedesktop/portal/desktop";
    const string Interface = "org.freedesktop.portal.Settings";
    const string Appearance = "org.freedesktop.appearance";
    const string Key = "reduced-motion";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    internal static async Task WatchAsync(Action<bool?> publish, CancellationToken cancellation) {
        try {
            if (DBusAddress.Session is not { } address) return;
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(Timeout, TimeProvider.System, cancellation).ConfigureAwait(false);
            var gate = new object();
            var signalReceived = false;
            using var watch = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Destination, Path = Path,
                    Interface = Interface, Member = "SettingChanged", Arg0 = Appearance },
                static (message, _) => {
                    var reader = message.GetBodyReader();
                    return (reader.ReadString(), reader.ReadString(), reader.ReadVariantValue());
                },
                notification => {
                    if (!notification.HasValue) return;
                    var (group, key, value) = notification.Value;
                    if (group != Appearance || key != Key) return;
                    lock (gate) {
                        signalReceived = true;
                        publish(Decode(value));
                    }
                }, emitOnCapturedContext: false).AsTask().WaitAsync(Timeout, TimeProvider.System, cancellation).ConfigureAwait(false);

            var initial = await ReadAsync(connection, cancellation).ConfigureAwait(false);
            lock (gate) {
                if (!signalReceived) publish(initial);
            }
            await connection.DisconnectedAsync().WaitAsync(cancellation).ConfigureAwait(false);
        } catch (Exception ex) when (ex is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException or ArgumentException) {
            // The setting is unavailable when the session bus or portal cannot answer.
        } finally {
            if (!cancellation.IsCancellationRequested) publish(null);
        }
    }

    static async Task<bool?> ReadAsync(DBusConnection connection, CancellationToken cancellation) {
        try {
            return await CallAsync(connection, "ReadOne", cancellation).ConfigureAwait(false);
        } catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod") {
            try {
                return await CallAsync(connection, "Read", cancellation).ConfigureAwait(false);
            } catch (DBusErrorReplyException) {
                return null;
            }
        } catch (DBusErrorReplyException) {
            return null;
        }
    }

    static Task<bool?> CallAsync(DBusConnection connection, string method, CancellationToken cancellation) {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: Path, @interface: Interface, member: method, signature: "ss");
        writer.WriteString(Appearance);
        writer.WriteString(Key);
        return connection.CallMethodAsync(writer.CreateMessage(),
            static (message, _) => Decode(message.GetBodyReader().ReadVariantValue()))
            .WaitAsync(Timeout, TimeProvider.System, cancellation);
    }

    internal static bool? Decode(VariantValue value) {
        if (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
        return value.Type == VariantValueType.UInt32 ? value.GetUInt32() == 1 : null;
    }
}
