using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Config;

/// <summary>The third-party hosts a profile calls and links to, each defaulting to the public service.</summary>
public record HostsSettings {
    public const string DefaultTelemetry   = "https://phog.kurrent.io";
    public const string DefaultNpmRegistry = "https://registry.npmjs.org/";
    public const string DefaultDocs        = "https://capacitor.kurrent.io/docs/";

    /// <summary>The PostHog host usage data is sent to.</summary>
    [JsonPropertyName("telemetry")]
    public string? Telemetry { get; init; }

    /// <summary>The npm registry the update check reads the published version from.</summary>
    [JsonPropertyName("npm_registry")]
    public string? NpmRegistry { get; init; }

    /// <summary>The base the CLI's documentation links append to.</summary>
    [JsonPropertyName("docs")]
    public string? Docs { get; init; }

    public static string EffectiveTelemetry(HostsSettings? hosts) => Named(hosts?.Telemetry)?.TrimEnd('/') ?? DefaultTelemetry;

    /// <summary>A base address: it ends in <c>/</c>, so a relative path extends it rather than replacing its last segment.</summary>
    public static string EffectiveNpmRegistry(HostsSettings? hosts) => Based(hosts?.NpmRegistry) ?? DefaultNpmRegistry;

    /// <inheritdoc cref="EffectiveNpmRegistry"/>
    public static string EffectiveDocs(HostsSettings? hosts) => Based(hosts?.Docs) ?? DefaultDocs;

    /// <summary>Whether <paramref name="value"/> can be one of these hosts: an absolute http or https URL with no query
    /// or fragment, since a route appended to a base lands inside them and drops the base's own path.</summary>
    public static bool IsValid(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
     && uri.Scheme is "http" or "https"
     && uri.Query.Length == 0
     && uri.Fragment.Length == 0;

    /// <summary>A stored value the setter would refuse, hand-edited into the file, reads as unset.</summary>
    static string? Named(string? value) => string.IsNullOrWhiteSpace(value) || !IsValid(value) ? null : value.Trim();

    static string? Based(string? value) => Named(value) is { } named ? named.TrimEnd('/') + "/" : null;
}
