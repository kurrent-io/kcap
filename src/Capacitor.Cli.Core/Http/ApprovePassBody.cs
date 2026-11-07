using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary><see cref="Via"/> is where the sender answered: <c>agent</c> or <c>cli</c> from here.</summary>
public record ApprovePassBody([property: JsonPropertyName("via")] string Via);
