using System.Text.Json;

namespace Capacitor.Cli.Core.Http;

public static class HttpReadFailures {
    /// <summary>Whether a request or its body read failed because of the server or the connection to it.
    /// The caller's own cancellation is not: reported as a failed read it turns a teardown into an outage
    /// that never happened. HttpClient's timeout is the same exception type with <paramref name="ct"/>
    /// unsignalled, and that one is a failure. Only the outermost exception's type is consulted.</summary>
    public static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}
