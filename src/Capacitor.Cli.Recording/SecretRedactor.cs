using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Transcripts;
using Capacitor.Cli.Recording.Capture;

namespace Capacitor.Cli.Recording;

public static partial class SecretRedactor {
    // Only malformed input uses this smaller textual-fallback limit.
    internal const int MaxRedactableLineChars = 64 * 1024;
    internal const int MaxRecordBytes = CaptureRule.MaxRecordBytes;

    internal const string RedactedMarker = "[REDACTED]";

    internal const string OversizeLinePlaceholder =
        $$"""{"type":"{{CaptureLossLine.OversizeType}}","reason":"line exceeded SecretRedactor size limit"}""";

    internal const string UnparsableOutputPlaceholder =
        $$"""{"type":"{{CaptureLossLine.UnparsableType}}","reason":"redacted line was no longer valid JSON"}""";

    internal const int WatcherMatchTimeoutMs = 100;

    // With the record budget this holds a line for about 1.4 s at most, inside the shortest cap a
    // hook puts around a drain, about 2 s.
    internal const int InlineRetryMatchTimeoutMs = 400;

    // A deadline only a super-linear pattern reaches: the linear patterns together cost about
    // 90 ns per char, so a 16M-char value takes under two seconds, while the JSON-key pattern is
    // quadratic on a quote-less run of keywords and would otherwise never return.
    internal const int OutOfProcessMatchTimeoutMs = 30_000;

    // One vocabulary under three deadlines. A generated pattern's deadline is fixed at compile
    // time, so each set declares its own; a copy built at runtime reads IgnoreCase by the culture.
    public static readonly SecretPatterns WatcherPatterns = new() {
        SecretKeyNameRegex       = SecretKeyNameRx(),
        PemBlockRegex            = PemBlockRx(),
        AwsUniqueIdRegex         = AwsUniqueIdRx(),
        VendorTokenRegex         = VendorTokenRx(),
        JsonKeySecretRegex       = JsonKeySecretRx(),
        EnvVarSecretRegex        = EnvVarSecretRx(),
        YamlStyleSecretRegex     = YamlStyleSecretRx(),
        ConnectionStringPwdRegex = ConnectionStringPwdRx(),
        AuthHeaderRegex          = AuthHeaderRx(),
        LabeledSecretRegex       = LabeledSecretRx(),
        UrlQuerySecretRegex      = UrlQuerySecretRx(),
        UrlUserinfoRegex         = UrlUserinfoRx()
    };

    internal static readonly SecretPatterns InlineRetryPatterns = new() {
        SecretKeyNameRegex       = SecretKeyNameRetryRx(),
        PemBlockRegex            = PemBlockRetryRx(),
        AwsUniqueIdRegex         = AwsUniqueIdRetryRx(),
        VendorTokenRegex         = VendorTokenRetryRx(),
        JsonKeySecretRegex       = JsonKeySecretRetryRx(),
        EnvVarSecretRegex        = EnvVarSecretRetryRx(),
        YamlStyleSecretRegex     = YamlStyleSecretRetryRx(),
        ConnectionStringPwdRegex = ConnectionStringPwdRetryRx(),
        AuthHeaderRegex          = AuthHeaderRetryRx(),
        LabeledSecretRegex       = LabeledSecretRetryRx(),
        UrlQuerySecretRegex      = UrlQuerySecretRetryRx(),
        UrlUserinfoRegex         = UrlUserinfoRetryRx()
    };

    internal static readonly SecretPatterns OutOfProcessPatterns = new() {
        SecretKeyNameRegex       = SecretKeyNameOutOfProcessRx(),
        PemBlockRegex            = PemBlockOutOfProcessRx(),
        AwsUniqueIdRegex         = AwsUniqueIdOutOfProcessRx(),
        VendorTokenRegex         = VendorTokenOutOfProcessRx(),
        JsonKeySecretRegex       = JsonKeySecretOutOfProcessRx(),
        EnvVarSecretRegex        = EnvVarSecretOutOfProcessRx(),
        YamlStyleSecretRegex     = YamlStyleSecretOutOfProcessRx(),
        ConnectionStringPwdRegex = ConnectionStringPwdOutOfProcessRx(),
        AuthHeaderRegex          = AuthHeaderOutOfProcessRx(),
        LabeledSecretRegex       = LabeledSecretOutOfProcessRx(),
        UrlQuerySecretRegex      = UrlQuerySecretOutOfProcessRx(),
        UrlUserinfoRegex         = UrlUserinfoOutOfProcessRx()
    };

    public static string RedactLine(string rawJsonlLine) => RedactLineWithOutcome(rawJsonlLine).Line;

    public static RedactionOutcome RedactLineWithOutcome(string rawJsonlLine) =>
        RedactLineWithOutcome(rawJsonlLine, TimeProvider.System);

    internal static RedactionOutcome RedactLineWithOutcome(string rawJsonlLine, TimeProvider time) =>
        RedactLineWithOutcome(rawJsonlLine, new RedactionBudget(time), WatcherPatterns);

    internal static RedactionOutcome RedactLineWithOutcome(string rawJsonlLine, RedactionBudget budget, SecretPatterns patterns) {
        var bytes = Encoding.UTF8.GetByteCount(rawJsonlLine);
        if (bytes > MaxRecordBytes) return Lost(RedactionLossReason.InputLimit);
        try {
            string line;
            RedactionLossReason? loss = null;
            try {
                line = RedactJsonStringValues(rawJsonlLine, bytes, budget, patterns) ?? rawJsonlLine;
            } catch (JsonException) {
                if (rawJsonlLine.Length > MaxRedactableLineChars)
                    return Lost(RedactionLossReason.MalformedInput);
                line = patterns.RedactSecrets(rawJsonlLine, budget);
                loss = RedactionLossReason.MalformedInput;
            }
            budget.Check();
            if (Encoding.UTF8.GetByteCount(line) > MaxRecordBytes) return Lost(RedactionLossReason.OutputLimit);
            return new RedactionOutcome(line, loss, rawJsonlLine.Length, bytes);
        } catch (RegexMatchTimeoutException) {
            return Lost(RedactionLossReason.RegexTimeout);
        } catch (RedactionBudgetExceededException) {
            return Lost(RedactionLossReason.RecordBudget);
        } catch (RedactionOutputLimitException) {
            return Lost(RedactionLossReason.OutputLimit);
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return Lost(RedactionLossReason.MalformedInput);
        }

        RedactionOutcome Lost(RedactionLossReason reason) => new(
            rawJsonlLine.Length > MaxRedactableLineChars ? OversizeLinePlaceholder : UnparsableOutputPlaceholder,
            reason, rawJsonlLine.Length, bytes);
    }

    public static bool IsSecretKey(ReadOnlySpan<char> propertyName) =>
        OutOfProcessPatterns.IsSecretKey(propertyName, RedactionBudget.Unlimited);

    public static string? RedactValue(ReadOnlySpan<char> value, bool keyIsSecret) =>
        OutOfProcessPatterns.Redact(value, keyIsSecret, RedactionBudget.Unlimited);

    static string? RedactJsonStringValues(string line, int byteCount, RedactionBudget budget, SecretPatterns patterns) {
        if (line.Length == 0) return null;

        var input = ArrayPool<byte>.Shared.Rent(byteCount);
        var decoded = ArrayPool<char>.Shared.Rent(line.Length);

        try {
            Encoding.UTF8.GetBytes(line, input.AsSpan());
            if (!Rewrite(input.AsSpan(0, byteCount), decoded, null, budget, patterns)) return null;
            using var output = new BoundedJsonBufferWriter(MaxRecordBytes);
            using var writer = new Utf8JsonWriter(output, WriterOptions);
            Rewrite(input.AsSpan(0, byteCount), decoded, writer, budget, patterns);
            return Encoding.UTF8.GetString(output.WrittenMemory.Span);
        } finally {
            ArrayPool<char>.Shared.Return(decoded, clearArray: true);
            ArrayPool<byte>.Shared.Return(input, clearArray: true);
        }
    }

    // Scan first so an unchanged record keeps its original encoding and cannot exceed an output cap.
    static bool Rewrite(ReadOnlySpan<byte> utf8, Span<char> decoded, Utf8JsonWriter? writer, RedactionBudget budget, SecretPatterns patterns) {
        var reader = new Utf8JsonReader(utf8, CaptureRule.ReaderOptions);

        var redactedAny = false;
        var keyIsSecret = false;
        var redactedKeys = 0;

        var secretDepth = -1;

        while (reader.Read()) {
            budget.Check();
            if (reader.CurrentDepth >= CaptureRule.MaxDepth && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                throw new InvalidOperationException("JSON depth exceeds the processing limit.");
            var inSecret = secretDepth >= 0;

            switch (reader.TokenType) {
                case JsonTokenType.PropertyName:
                    var name = decoded[..reader.CopyString(decoded)];
                    keyIsSecret = inSecret || patterns.IsSecretKey(name, budget);

                    if (patterns.IsSecretItself(name, budget)) {
                        writer?.WritePropertyName($"{RedactedMarker}-{++redactedKeys}");
                        redactedAny = true;
                    } else {
                        writer?.WritePropertyName(JsonEncodedText.Encode(name, WriterOptions.Encoder));
                    }

                    continue;

                case JsonTokenType.String:
                    var value = decoded[..reader.CopyString(decoded)];
                    if (patterns.Redact(value, keyIsSecret || inSecret, budget) is { } clean) {
                        writer?.WriteStringValue(JsonEncodedText.Encode(clean, WriterOptions.Encoder));
                        redactedAny = true;
                    } else {
                        writer?.WriteStringValue(JsonEncodedText.Encode(value, WriterOptions.Encoder));
                    }

                    break;

                // Token counters match secret key names, but numeric values must retain accounting.
                case JsonTokenType.Number:
                    writer?.WriteRawValue(reader.ValueSpan, skipInputValidation: true);

                    break;

                case JsonTokenType.StartObject:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer?.WriteStartObject();

                    break;

                case JsonTokenType.StartArray:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer?.WriteStartArray();

                    break;

                case JsonTokenType.EndObject:
                    writer?.WriteEndObject();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                case JsonTokenType.EndArray:
                    writer?.WriteEndArray();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                case JsonTokenType.Comment:
                    redactedAny = true;

                    continue;

                case JsonTokenType.True:  writer?.WriteBooleanValue(true); break;
                case JsonTokenType.False: writer?.WriteBooleanValue(false); break;
                case JsonTokenType.Null:  writer?.WriteNullValue(); break;

                default: throw new InvalidOperationException($"Unhandled JSON token {reader.TokenType}.");
            }

            keyIsSecret = false;
        }

        writer?.Flush();

        return redactedAny;
    }

    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    const string SecretKeywords =
        "secrets?|tokens?|passwords?|passwd|pwd|api[-_.]?keys?|private[-_.]?keys?|credentials?|client[-_.]?secrets?|access[-_.]?keys?|auth[-_.]?tokens?";

    const string AuthHeaderNames =
        "authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token|x-access-token|x-amz-security-token|x-amz-signature|x-goog-api-key|api-key|private-token|job-token|deploy-token|x-vault-token|x-consul-token|x-csrf-token|x-xsrf-token|x-hub-signature(?:-256)?|x-slack-signature|stripe-signature|x-registry-auth";

    // Only an all-caps name may end in a bare KEY or PASS: in lowercase that suffix names a
    // database or map key far more often than a credential.
    const string UpperSecretSuffixName = "(?-i:[A-Z0-9_]*(?:KEYS?|PASS))";

    const string SecretKeyNamePattern =
        "(?:" + AuthHeaderNames + "|auth)$|(?:" + SecretKeywords + ")|^" + UpperSecretSuffixName + "$";

    [GeneratedRegex(SecretKeyNamePattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex SecretKeyNameRx();

    [GeneratedRegex(SecretKeyNamePattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex SecretKeyNameRetryRx();

    [GeneratedRegex(SecretKeyNamePattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex SecretKeyNameOutOfProcessRx();

    const string PemBlockPattern =
        @"-----BEGIN[A-Z\s]*PRIVATE KEY-----[\s\S]{0,16384}?-----END[A-Z\s]*PRIVATE KEY-----";

    [GeneratedRegex(PemBlockPattern, RegexOptions.None, WatcherMatchTimeoutMs)]
    private static partial Regex PemBlockRx();

    [GeneratedRegex(PemBlockPattern, RegexOptions.None, InlineRetryMatchTimeoutMs)]
    private static partial Regex PemBlockRetryRx();

    [GeneratedRegex(PemBlockPattern, RegexOptions.None, OutOfProcessMatchTimeoutMs)]
    private static partial Regex PemBlockOutOfProcessRx();

    const string AwsUniqueIdPattern =
        "(?:AKIA|ASIA|AROA|AIDA|AIPA|AGPA|ANPA|ANVA|ASCA|APKA|ABIA|ACCA)[0-9A-Z]{16,128}(?![0-9A-Z])";

    [GeneratedRegex(AwsUniqueIdPattern, RegexOptions.None, WatcherMatchTimeoutMs)]
    private static partial Regex AwsUniqueIdRx();

    [GeneratedRegex(AwsUniqueIdPattern, RegexOptions.None, InlineRetryMatchTimeoutMs)]
    private static partial Regex AwsUniqueIdRetryRx();

    [GeneratedRegex(AwsUniqueIdPattern, RegexOptions.None, OutOfProcessMatchTimeoutMs)]
    private static partial Regex AwsUniqueIdOutOfProcessRx();

    // Anchored: SecretPatterns finds candidates by prefix and confirms each one here.
    internal const string VendorTokenPattern =
        @"\G(?:ghp_|gho_|ghs_|github_pat_|cfat_|(?<![A-Za-z0-9])sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-|dckr_pat_|dckr_oat_)[A-Za-z0-9\-_]{10,}";

    [GeneratedRegex(VendorTokenPattern, RegexOptions.None, WatcherMatchTimeoutMs)]
    private static partial Regex VendorTokenRx();

    [GeneratedRegex(VendorTokenPattern, RegexOptions.None, InlineRetryMatchTimeoutMs)]
    private static partial Regex VendorTokenRetryRx();

    [GeneratedRegex(VendorTokenPattern, RegexOptions.None, OutOfProcessMatchTimeoutMs)]
    private static partial Regex VendorTokenOutOfProcessRx();

    const string JsonKeySecretPattern =
        """((?:\\"|")(?:[^"\\]*(?:""" + SecretKeywords + """)[^"\\]*|""" + UpperSecretSuffixName + """)(?:\\"|")[ \t]*:[ \t]*(?:\\"|"))([^"\\]+)((?:\\"|")|$)""";

    [GeneratedRegex(JsonKeySecretPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex JsonKeySecretRx();

    [GeneratedRegex(JsonKeySecretPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex JsonKeySecretRetryRx();

    [GeneratedRegex(JsonKeySecretPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex JsonKeySecretOutOfProcessRx();

    // The match starts at `=` and finds the name behind it, so letters without one cost nothing;
    // the name is the whole [A-Za-z_] run before it, so a digit ends it, as in OAUTH2_TOKEN.
    internal const string EnvVarSecretPattern =
        @"(?<=(?<![A-Za-z_])(?:[A-Z_]*(?:SECRETS?|TOKENS?|PASSWORDS?|PASSWD|PWD|API_?KEYS?|PRIVATE_?KEYS?|CREDENTIALS?|CLIENT_?SECRETS?|ACCESS_?KEYS?|AUTH_?TOKENS?)[A-Z_]*|(?-i:[A-Z_]*(?:KEYS?|PASS))))=([^\s""\\]+)";

    [GeneratedRegex(EnvVarSecretPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex EnvVarSecretRx();

    [GeneratedRegex(EnvVarSecretPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex EnvVarSecretRetryRx();

    [GeneratedRegex(EnvVarSecretPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex EnvVarSecretOutOfProcessRx();

    const string YamlStyleSecretPattern = "((?:" + SecretKeywords + """)[\w.\-]*:[ \t]+)([^\s"\\]{8,})""";

    [GeneratedRegex(YamlStyleSecretPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex YamlStyleSecretRx();

    [GeneratedRegex(YamlStyleSecretPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex YamlStyleSecretRetryRx();

    [GeneratedRegex(YamlStyleSecretPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex YamlStyleSecretOutOfProcessRx();

    const string ConnectionStringPwdPattern = @"((?:Password|Pwd)\s*=\s*)([^;""\\]+)(;|$)";

    [GeneratedRegex(ConnectionStringPwdPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex ConnectionStringPwdRx();

    [GeneratedRegex(ConnectionStringPwdPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex ConnectionStringPwdRetryRx();

    [GeneratedRegex(ConnectionStringPwdPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex ConnectionStringPwdOutOfProcessRx();

    const string AuthHeaderPattern = "((?:" + AuthHeaderNames + """)(?:\\?")?\s*:\s*(?:\\?")?\s*)([^\r\n"\\]+)""";

    [GeneratedRegex(AuthHeaderPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex AuthHeaderRx();

    [GeneratedRegex(AuthHeaderPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex AuthHeaderRetryRx();

    [GeneratedRegex(AuthHeaderPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex AuthHeaderOutOfProcessRx();

    const string LabeledSecretPattern = """\b((?:""" + SecretKeywords + """)\b[ \t]+)([^\s"\\]{16,})""";

    [GeneratedRegex(LabeledSecretPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex LabeledSecretRx();

    [GeneratedRegex(LabeledSecretPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex LabeledSecretRetryRx();

    [GeneratedRegex(LabeledSecretPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex LabeledSecretOutOfProcessRx();

    const string UrlQuerySecretPattern =
        """([?&](?:access_token|refresh_token|id_token|client_secret|signature|sig|x-amz-signature|awsaccesskeyid|api_key|apikey|api-key|token|password|secret|auth_token|sas)=)([^&\s"\\#]+)""";

    [GeneratedRegex(UrlQuerySecretPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex UrlQuerySecretRx();

    [GeneratedRegex(UrlQuerySecretPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex UrlQuerySecretRetryRx();

    [GeneratedRegex(UrlQuerySecretPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex UrlQuerySecretOutOfProcessRx();

    // Redis-family clients read a colon-free userinfo as the password, in every scheme variant
    // (rediss, redis+tls, redis-sentinel, valkey); elsewhere a bare userinfo is only a username.
    // The password runs to the authority's last @, since clients accept one unencoded, but a
    // segment after an @ holds only RFC 3986 userinfo characters and never crosses a , or ; that
    // opens a key= field, so a later field's address stays out of it. JSON
    // text held in a string can escape the delimiters as \/ and \u0040.
    const string UrlUserinfoPattern =
        """((?<![A-Za-z0-9+.\-])(?:[A-Za-z][A-Za-z0-9+.\-]*:(?:\\?/){2}[^:/?#\s"\\@]*:|(?:redis|valkey)[A-Za-z0-9+.\-]*:(?:\\?/){2}))([^\s"\\/?#@]+(?:(?:@|\\u0040)(?:[A-Za-z0-9\-._~%!$&'()*+=:]|[,;](?![A-Za-z0-9\-._~%!$&'()*+:]*=))+)*)(@|\\u0040)""";

    [GeneratedRegex(UrlUserinfoPattern, RegexOptions.IgnoreCase, WatcherMatchTimeoutMs)]
    private static partial Regex UrlUserinfoRx();

    [GeneratedRegex(UrlUserinfoPattern, RegexOptions.IgnoreCase, InlineRetryMatchTimeoutMs)]
    private static partial Regex UrlUserinfoRetryRx();

    [GeneratedRegex(UrlUserinfoPattern, RegexOptions.IgnoreCase, OutOfProcessMatchTimeoutMs)]
    private static partial Regex UrlUserinfoOutOfProcessRx();
}
