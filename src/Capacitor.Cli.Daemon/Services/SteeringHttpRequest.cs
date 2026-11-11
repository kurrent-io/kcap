namespace Capacitor.Cli.Daemon.Services;

/// <summary>One request read off the steering socket. <see cref="Path"/> carries no query string.</summary>
internal sealed record SteeringHttpRequest(string Method, string Path, byte[] Body);
