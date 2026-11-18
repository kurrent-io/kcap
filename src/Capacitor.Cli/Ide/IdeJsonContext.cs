using System.Text.Json.Serialization;

namespace Capacitor.Cli.Ide;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(UnsupportedProtocolData))]
[JsonSerializable(typeof(ConnectionChanged))]
[JsonSerializable(typeof(SubscribeParams))]
[JsonSerializable(typeof(UnsubscribeParams))]
[JsonSerializable(typeof(SetScopeParams))]
[JsonSerializable(typeof(ScopeChanged))]
[JsonSerializable(typeof(SessionsSnapshot))]
[JsonSerializable(typeof(SessionsChanged))]
internal partial class IdeJsonContext : JsonSerializerContext;
