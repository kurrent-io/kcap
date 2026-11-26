namespace Capacitor.Cli.Core.RepoInstructions;

/// <param name="Files">What fits the upload limits: root files in tree order, then their imports.</param>
/// <param name="Skipped">One line per file left out, and why.</param>
public sealed record RepoInstructionSet(IReadOnlyList<RepoInstructionFile> Files, string Digest, IReadOnlyList<string> Skipped);
