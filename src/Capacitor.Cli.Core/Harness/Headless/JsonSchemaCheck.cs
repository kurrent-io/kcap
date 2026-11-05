using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Validates a JSON value against the JSON Schema vocabulary the headless runners support. No
/// reflection and no package: a keyword outside the vocabulary is reported by
/// <see cref="UnsupportedKeyword"/> so a caller can refuse the schema rather than leave a
/// constraint unchecked.
/// </summary>
internal static class JsonSchemaCheck {
    const int MaxDepth = 64;
    static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    static readonly HashSet<string> Validated = [
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "anyOf", "oneOf", "allOf", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "minItems", "maxItems", "pattern", "$ref",
    ];

    static readonly HashSet<string> Containers = ["$defs", "definitions"];

    static readonly HashSet<string> Annotations = [
        "description", "title", "default", "examples", "format", "$schema", "$id", "$comment",
        "readOnly", "writeOnly", "deprecated",
    ];

    static readonly HashSet<string> TypeNames = ["string", "number", "integer", "boolean", "object", "array", "null"];

    /// The first keyword outside the supported vocabulary, or one whose value is not what the keyword takes; "schema" when
    /// the value itself is neither an object nor a boolean, or is nested too deep to check. Null when fully supported.
    public static string? UnsupportedKeyword(JsonElement schema) =>
        IsSchema(schema) ? Unsupported(schema, schema, 0) : "schema";

    /// A schema the gate refuses matches nothing, so a shape it refuses never reads as unconstrained.
    public static bool Matches(JsonElement schema, JsonElement instance) =>
        UnsupportedKeyword(schema) is null && Validate(schema, schema, instance, 0);

    static bool IsSchema(JsonElement value) => value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;

    static string? Unsupported(JsonElement root, JsonElement schema, int depth) {
        if (schema.ValueKind is not JsonValueKind.Object) return null;
        if (depth > MaxDepth) return "schema";

        foreach (var p in schema.EnumerateObject()) {
            var name = p.Name;
            if (Annotations.Contains(name)) continue;
            if (!Containers.Contains(name) && !Validated.Contains(name)) return name;
            if (UnsupportedValue(root, name, p.Value, depth) is { } found) return found;
        }

        return null;
    }

    static string? UnsupportedValue(JsonElement root, string name, JsonElement value, int depth) {
        switch (name) {
            case "$ref":
                return value.ValueKind is JsonValueKind.String
                    && TryParseRef(value.GetString()!, out var container, out var target)
                    && Lookup(root, container, target) is not null
                        ? null
                        : name;
            case "properties" or "$defs" or "definitions":
                if (value.ValueKind is not JsonValueKind.Object) return name;
                foreach (var member in value.EnumerateObject())
                    if (UnsupportedSubschema(root, name, member.Value, depth) is { } inner) return inner;
                return null;
            case "items" or "additionalProperties":
                return UnsupportedSubschema(root, name, value, depth);
            case "anyOf" or "oneOf" or "allOf":
                if (value.ValueKind is not JsonValueKind.Array || value.GetArrayLength() == 0) return name;
                foreach (var sub in value.EnumerateArray())
                    if (UnsupportedSubschema(root, name, sub, depth) is { } inner) return inner;
                return null;
            case "required":
                return IsRequiredList(value) ? null : name;
            case "type":
                return IsTypeName(value) || IsTypeList(value) ? null : name;
            case "enum":
                return value.ValueKind is JsonValueKind.Array && value.GetArrayLength() > 0 ? null : name;
            case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                return value.ValueKind is JsonValueKind.Number ? null : name;
            case "minLength" or "maxLength" or "minItems" or "maxItems":
                return TryBound(value, out _) ? null : name;
            case "pattern":
                return IsPattern(value) ? null : name;
            default:
                return null;
        }
    }

    static string? UnsupportedSubschema(JsonElement root, string keyword, JsonElement value, int depth) =>
        IsSchema(value) ? Unsupported(root, value, depth + 1) : keyword;

    static bool IsTypeName(JsonElement value) => value.ValueKind is JsonValueKind.String && TypeNames.Contains(value.GetString()!);

    static bool IsTypeList(JsonElement value) =>
        value.ValueKind is JsonValueKind.Array && value.GetArrayLength() > 0 && value.EnumerateArray().All(IsTypeName);

    static bool IsPattern(JsonElement value) {
        if (value.ValueKind is not JsonValueKind.String) return false;
        try {
            _ = new Regex(value.GetString()!, RegexOptions.None, PatternTimeout);
            return true;
        } catch (ArgumentException) {
            return false;
        }
    }

    // A local reference is "#" or "#/$defs/<name>" / "#/definitions/<name>" with JSON-pointer escapes.
    static bool TryParseRef(string reference, out string? container, out string? name) {
        container = null;
        name = null;
        if (reference == "#") return true;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;

        var parts = reference[2..].Split('/');
        if (parts.Length != 2 || !Containers.Contains(parts[0])) return false;

        container = parts[0];
        name = parts[1].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return true;
    }

    static JsonElement? Lookup(JsonElement root, string? container, string? name) {
        if (container is null) return root;
        if (root.ValueKind is JsonValueKind.Object
            && root.TryGetProperty(container, out var defs)
            && defs.ValueKind is JsonValueKind.Object
            && defs.TryGetProperty(name!, out var found))
            return found;
        return null;
    }

    static bool Validate(JsonElement root, JsonElement schema, JsonElement value, int depth) {
        if (depth > MaxDepth) return false;

        switch (schema.ValueKind) {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Object: break;
            default: return false;
        }

        foreach (var p in schema.EnumerateObject()) {
            var ok = p.Name switch {
                "type"                 => TypeMatches(p.Value, value),
                "properties"           => PropertiesMatch(root, p.Value, value, depth),
                "required"             => RequiredPresent(p.Value, value),
                "additionalProperties" => AdditionalMatch(root, schema, p.Value, value, depth),
                "items"                => value.ValueKind is not JsonValueKind.Array
                                          || value.EnumerateArray().All(e => Validate(root, p.Value, e, depth + 1)),
                "enum"                 => p.Value.ValueKind is JsonValueKind.Array && p.Value.EnumerateArray().Any(e => Equal(e, value)),
                "const"                => Equal(p.Value, value),
                "anyOf"                => p.Value.ValueKind is JsonValueKind.Array && p.Value.EnumerateArray().Any(s => Validate(root, s, value, depth + 1)),
                "oneOf"                => p.Value.ValueKind is JsonValueKind.Array && p.Value.EnumerateArray().Count(s => Validate(root, s, value, depth + 1)) == 1,
                "allOf"                => p.Value.ValueKind is JsonValueKind.Array && p.Value.EnumerateArray().All(s => Validate(root, s, value, depth + 1)),
                "minimum"              => Compare(p.Value, value, d => d >= 0),
                "maximum"              => Compare(p.Value, value, d => d <= 0),
                "exclusiveMinimum"     => Compare(p.Value, value, d => d > 0),
                "exclusiveMaximum"     => Compare(p.Value, value, d => d < 0),
                "minLength"            => Length(p.Value, value, (len, bound) => len >= bound),
                "maxLength"            => Length(p.Value, value, (len, bound) => len <= bound),
                "minItems"             => ItemCount(p.Value, value, (n, bound) => n >= bound),
                "maxItems"             => ItemCount(p.Value, value, (n, bound) => n <= bound),
                "pattern"              => PatternMatches(p.Value, value),
                "$ref"                 => RefMatches(root, p.Value, value, depth),
                _                      => true,
            };
            if (!ok) return false;
        }

        return true;
    }

    static bool RefMatches(JsonElement root, JsonElement reference, JsonElement value, int depth) {
        if (reference.ValueKind is not JsonValueKind.String || !TryParseRef(reference.GetString()!, out var container, out var name))
            return false;
        return Lookup(root, container, name) is { } target && Validate(root, target, value, depth + 1);
    }

    static bool TypeMatches(JsonElement types, JsonElement value) => types.ValueKind switch {
        JsonValueKind.String => IsType(types.GetString()!, value),
        JsonValueKind.Array  => types.EnumerateArray().Any(t => t.ValueKind is JsonValueKind.String && IsType(t.GetString()!, value)),
        _                    => false,
    };

    static bool IsType(string type, JsonElement value) => type switch {
        "string"  => value.ValueKind is JsonValueKind.String,
        "number"  => value.ValueKind is JsonValueKind.Number,
        "integer" => value.ValueKind is JsonValueKind.Number && IsWhole(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object"  => value.ValueKind is JsonValueKind.Object,
        "array"   => value.ValueKind is JsonValueKind.Array,
        "null"    => value.ValueKind is JsonValueKind.Null,
        _         => false,
    };

    static bool IsWhole(JsonElement number) {
        if (number.TryGetInt64(out _)) return true;
        return number.TryGetDouble(out var d) && double.IsFinite(d) && Math.Floor(d) == d;
    }

    static bool PropertiesMatch(JsonElement root, JsonElement properties, JsonElement value, int depth) {
        if (properties.ValueKind is not JsonValueKind.Object) return false;
        if (value.ValueKind is not JsonValueKind.Object) return true;
        foreach (var prop in properties.EnumerateObject())
            if (value.TryGetProperty(prop.Name, out var member) && !Validate(root, prop.Value, member, depth + 1))
                return false;
        return true;
    }

    static bool RequiredPresent(JsonElement required, JsonElement value) {
        if (!IsRequiredList(required)) return false;
        if (value.ValueKind is not JsonValueKind.Object) return true;
        return required.EnumerateArray().All(name => value.TryGetProperty(name.GetString()!, out _));
    }

    static bool IsRequiredList(JsonElement required) =>
        required.ValueKind is JsonValueKind.Array && required.EnumerateArray().All(name => name.ValueKind is JsonValueKind.String);

    static bool AdditionalMatch(JsonElement root, JsonElement schema, JsonElement additional, JsonElement value, int depth) {
        if (value.ValueKind is not JsonValueKind.Object) return true;
        var declared = schema.TryGetProperty("properties", out var props) && props.ValueKind is JsonValueKind.Object ? props : default;

        foreach (var member in value.EnumerateObject()) {
            if (declared.ValueKind is JsonValueKind.Object && declared.TryGetProperty(member.Name, out _)) continue;
            if (!Validate(root, additional, member.Value, depth + 1)) return false;
        }

        return true;
    }

    static bool Compare(JsonElement bound, JsonElement value, Func<int, bool> accept) {
        if (value.ValueKind is not JsonValueKind.Number) return true;
        if (bound.ValueKind is not JsonValueKind.Number || !bound.TryGetDouble(out var b) || !value.TryGetDouble(out var v)) return false;
        return accept(v.CompareTo(b));
    }

    static bool TryBound(JsonElement bound, out int n) {
        n = 0;
        return bound.ValueKind is JsonValueKind.Number && bound.TryGetInt32(out n) && n >= 0;
    }

    static bool Length(JsonElement bound, JsonElement value, Func<int, int, bool> accept) {
        if (value.ValueKind is not JsonValueKind.String) return true;
        if (!TryBound(bound, out var n)) return false;
        return accept(new StringInfo(value.GetString()!).LengthInTextElements, n);
    }

    static bool ItemCount(JsonElement bound, JsonElement value, Func<int, int, bool> accept) {
        if (value.ValueKind is not JsonValueKind.Array) return true;
        if (!TryBound(bound, out var n)) return false;
        return accept(value.GetArrayLength(), n);
    }

    // A pattern that is not a valid expression, or that exceeds the timeout, is a non-match: an
    // adversarial or malformed schema must not hang or crash the run.
    static bool PatternMatches(JsonElement pattern, JsonElement value) {
        if (value.ValueKind is not JsonValueKind.String) return true;
        if (pattern.ValueKind is not JsonValueKind.String) return false;
        try {
            return Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.None, PatternTimeout);
        } catch (RegexMatchTimeoutException) {
            return false;
        } catch (ArgumentException) {
            return false;
        }
    }

    static bool Equal(JsonElement a, JsonElement b) {
        if (a.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return a.ValueKind == b.ValueKind;
        if (a.ValueKind != b.ValueKind) return false;

        switch (a.ValueKind) {
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.TryGetDouble(out var x) && b.TryGetDouble(out var y) && x == y;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using (var ea = a.EnumerateArray().GetEnumerator())
                using (var eb = b.EnumerateArray().GetEnumerator()) {
                    while (ea.MoveNext() && eb.MoveNext())
                        if (!Equal(ea.Current, eb.Current)) return false;
                }
                return true;
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToList();
                if (left.Count != b.EnumerateObject().Count()) return false;
                return left.All(p => b.TryGetProperty(p.Name, out var other) && Equal(p.Value, other));
            default: return true;
        }
    }
}
