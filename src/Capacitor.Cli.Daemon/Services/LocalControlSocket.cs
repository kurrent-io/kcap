using System.Net.Sockets;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's local control socket, bound once and kept for the process. It is bound before the vendor probes, so a
/// client reaches a starting daemon instead of finding nothing; <see cref="StartingLocalControl"/> holds what arrives
/// until <see cref="LocalControlServer"/> takes the same socket over, with no gap in which a connect is refused.
/// The file is owner-only (0600): anything that can open it can spawn processes and stream a terminal.
/// </summary>
internal sealed class LocalControlSocket(DaemonConfig config) : IDisposable {
    readonly Lock _lock = new();
    Socket?       _listener;

    public string Path => config.Store.SocketPath(config.Name);

    /// <summary>The bound listener, binding it on the first call.</summary>
    public Socket Bind() {
        lock (_lock) {
            if (_listener is not null) return _listener;

            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* stale; bind fails loudly below */ }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                listener.Bind(new UnixDomainSocketEndPoint(Path));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(16);
            } catch {
                listener.Dispose();
                throw;
            }

            return _listener = listener;
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_listener is null) return;

            _listener.Dispose();
            _listener = null;
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}
