namespace Capacitor.Cli.Core;

/// <summary>The server's answer to <c>DaemonUpdateCapabilities</c>. Not applied means the calling connection holds no
/// daemon slot, so the server read nothing the update carried.</summary>
public sealed record DaemonCapabilitiesUpdateResponse(bool Applied);
