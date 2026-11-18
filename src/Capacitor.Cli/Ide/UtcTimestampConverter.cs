using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Ide;

/// <summary>The wire carries instants as ISO 8601 UTC with a <c>Z</c> suffix; the default writer would emit <c>+00:00</c>.</summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset> {
    const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
