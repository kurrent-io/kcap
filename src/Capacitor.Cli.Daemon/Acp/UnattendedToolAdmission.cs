using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Kiro;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Kiro can ask permission for an already trusted result tool, so unconditional failure would
/// prevent legitimate reports while unconditional approval would widen the reviewer's authority.
/// The frame has only a presentation title, not structured tool identity. Strip the verified prefix,
/// then require the entire title to equal one admitted id; substrings or surrounding text must fail.
/// An unguessable alias does not authenticate text the model can see and echo. This check necessarily
/// trusts Kiro to describe the requested call faithfully; a title-format change must fail closed.
/// </summary>
internal static class UnattendedToolAdmission {
    /// <summary>The one presentation prefix measured on kiro-cli 2.16.0. Stripped, never matched
    /// loosely: everything after it must BE an admitted id.</summary>
    const string TitlePrefix = "Running: ";

    /// <summary>The admitted identities: the SAME set the <c>--trust-tools</c> argv grants — native
    /// tools (<c>fs_read</c>, <c>thinking</c>) AND the <c>@server/tool</c> namespaced entries, from one
    /// shared builder so admission can't diverge from what was injected. The native half is
    /// load-bearing (the vendor leaks prompts even for trusted tools); <c>fs_write</c>/<c>execute_bash</c>
    /// are in neither set, so the read-only floor is unchanged.</summary>
    internal static IReadOnlySet<string> AdmittedFor(
            IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) =>
        KiroReviewerTrustList.NativeTools
            .Concat(KiroReviewerTrustList.NamespacedEntries(injected, identity))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when this frame's title is EXACTLY one admitted tool, modulo the measured prefix.
    /// <paramref name="toolCall"/> is the raw <c>toolCall</c> object from the request.
    /// </summary>
    internal static bool IsAdmitted(JsonElement toolCall, IReadOnlySet<string> admitted) {
        if (admitted.Count == 0) return false;

        var title = toolCall.ValueKind == JsonValueKind.Object
                 && toolCall.TryGetProperty("title", out var t)
                 && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

        if (title is not { Length: > 0 }) return false;

        // Ordinal throughout. A culture-aware comparison can equate strings that are different bytes,
        // which is the wrong behaviour for an identity check.
        var candidate = title.StartsWith(TitlePrefix, StringComparison.Ordinal)
            ? title[TitlePrefix.Length..]
            : title;

        return admitted.Contains(candidate);
    }
}
