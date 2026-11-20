namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The server's capability answer, from the hub's <c>IngestCapabilities</c> and <c>GET /api/ingest/capabilities</c>
/// alike: the canonical batch format it takes, or 0.</summary>
public readonly record struct CanonicalCapabilities(int CanonicalFormat);
