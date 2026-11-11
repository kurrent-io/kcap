using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A JSON answer on the steering socket, written as HTTP/1.1 on a connection that then closes.</summary>
internal sealed record SteeringHttpResponse(int Status, byte[] Body) {
    /// <summary>A body of <c>{"error": code}</c>.</summary>
    public static SteeringHttpResponse Error(int status, string code) {
        using var body = new MemoryStream();

        using (var json = new Utf8JsonWriter(body)) {
            json.WriteStartObject();
            json.WriteString("error", code);
            json.WriteEndObject();
        }

        return new(status, body.ToArray());
    }

    /// <summary>The whole answer, as it goes on the wire. A 204 carries no body and, as HTTP/1.1 requires, no length.</summary>
    public byte[] Encode() {
        if (Status == 204) return Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");

        var head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {Status} {Reason(Status)}\r\n{(Status == 405 ? "Allow: POST\r\n" : "")}Content-Type: application/json\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n");

        return [.. Encoding.ASCII.GetBytes(head), .. Body];
    }

    public async Task WriteAsync(Stream stream, CancellationToken ct) {
        await stream.WriteAsync(Encode(), ct);
        await stream.FlushAsync(ct);
    }

    static string Reason(int status) => status switch {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Content Too Large",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _   => "Status",
    };
}
