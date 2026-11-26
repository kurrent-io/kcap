namespace Capacitor.Cli.Core.RepoInstructions;

/// <param name="Files">Root files in tree order, then their imports, imports left out until the set fits the upload limits.</param>
/// <param name="Skipped">One line per file left out, and why.</param>
/// <param name="OverLimits">The root files alone exceed the upload limits, as several spellings of one name can.</param>
public sealed record RepoInstructionSet(IReadOnlyList<RepoInstructionFile> Files, string Digest, IReadOnlyList<string> Skipped, bool OverLimits = false);
