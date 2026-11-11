namespace Capacitor.Cli.Recording;

/// <summary>A transport carrying many sessions, one of which can be cut off for good while the rest go on.</summary>
public interface ISilenceableRecorderTransport : IRecorderTransport {
    /// <summary>Refuses every later call for the session, a stuck recorder's included, and answers once everything
    /// already sent for it has settled.</summary>
    /// <returns>Whether the server answered all of it. False when a call went with the connection, which may have
    /// reached the server all the same.</returns>
    Task<bool> SilenceAsync(string sessionId);

    /// <summary>Runs a call made for the session beside this transport, such as an HTTP post, as one of the session's own:
    /// refused once the session is silenced, and waited for by a silence. One that throws may have reached the server.</summary>
    Task CarryAsync(string sessionId, Func<Task> post);
}
