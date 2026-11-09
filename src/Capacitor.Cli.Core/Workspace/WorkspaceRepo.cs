using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>A working tree: its toplevel and the common git directory it shares with its linked worktrees.</summary>
public sealed record WorkspaceRepo(string Toplevel, string CommonDir) {
    /// <summary>Names the tree's span file, so one session working in two worktrees of one repository keeps two spans.</summary>
    public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{CommonDir}\n{Toplevel}")))[..16];
}
