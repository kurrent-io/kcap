namespace Capacitor.Cli.Core.Config;

/// <summary><c>experimental.json</c> as one stat sees it. Two equal fingerprints mean the file has not
/// changed between them; a missing file has a fingerprint of its own.</summary>
public readonly record struct ExperimentalSettingsFingerprint(bool Exists, DateTime Written, long Length);
