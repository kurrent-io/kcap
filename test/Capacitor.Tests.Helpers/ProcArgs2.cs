using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Builds a synthetic <c>KERN_PROCARGS2</c> payload (little-endian argc, then the
/// NUL-terminated exec path, then argv).
/// </summary>
public static class ProcArgs2 {
    public static byte[] Of(int argc, string execPath, params string[] argv) {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(argc));
        bytes.AddRange(Encoding.UTF8.GetBytes(execPath));
        bytes.Add(0);

        foreach (var a in argv) {
            bytes.AddRange(Encoding.UTF8.GetBytes(a));
            bytes.Add(0);
        }

        return bytes.ToArray();
    }
}
