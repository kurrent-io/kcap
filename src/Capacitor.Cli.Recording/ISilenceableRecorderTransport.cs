namespace Capacitor.Cli.Recording;

/// <summary>A transport carrying many sessions, one of which can be cut off for good while the rest go on.</summary>
public interface ISilenceableRecorderTransport : IRecorderTransport {
    /// <summary>Refuses every later call for the session, a stuck recorder's included, and answers once everything
    /// already sent for it has settled.</summary>
    /// <returns>Whether the server answered all of it. False when a call went with the connection, which may have
    /// reached the server all the same.</returns>
    Task<bool> SilenceAsync(string sessionId);
}
