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
[JsonSerializable(typeof(ContextSubscribeParams))]
[JsonSerializable(typeof(ContextRefParams))]
[JsonSerializable(typeof(ContextSnapshot))]
[JsonSerializable(typeof(ContextChanged))]
internal partial class IdeJsonContext : JsonSerializerContext;
