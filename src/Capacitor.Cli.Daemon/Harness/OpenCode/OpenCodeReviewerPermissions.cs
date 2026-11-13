using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Kiro;

namespace Capacitor.Cli.Daemon.Harness.OpenCode;

/// <summary>
/// The <c>OPENCODE_PERMISSION</c> document for one unattended OpenCode reviewer launch, the
/// equivalent of <see cref="KiroReviewerTrustList"/> in env form, since OpenCode has no trust argv.
///
/// <para>Deny-all, then exactly what a reviewer needs. A denied tool is absent from the model's
/// surface rather than refused when called, so no <c>session/request_permission</c> frame is ever
/// raised and <see cref="AcpUnattendedInteractionPolicy.Fail"/> holds: a frame means the launch
/// contract regressed (<c>docs/probes/2026-08-07-opencode-acp/</c>).</para>
///
/// <para>OpenCode presents an injected MCP tool flattened as <c>{serverName}_{toolName}</c> and puts
/// it through the same table, so without its server's entry the reviewer cannot reach its own result
/// channel. The entries are built from the injected specs, never from server ids: a second derivation
/// of the names fails silently.</para>
///
/// <para><c>read</c>/<c>grep</c>/<c>glob</c>/<c>list</c> are not path-scoped, so a reviewer can read
/// anything the daemon user can; <see cref="OpenCodeReviewerCapability"/> gates that consent.</para>
/// </summary>
internal static class OpenCodeReviewerPermissions {
    /// <summary>Native tools a reviewer needs, never <c>write</c>, <c>edit</c>, <c>patch</c> or
    /// <c>bash</c>. A misspelt name matches nothing and stays denied.</summary>
    internal static readonly ImmutableArray<string> ReadTools = ["read", "grep", "glob", "list"];

    /// <summary>An allowlist rather than a list of glob metacharacters: no character outside it can mean
    /// anything to whatever glob engine OpenCode uses.</summary>
    static bool IsAdmissibleServerNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    /// <summary>The <c>OPENCODE_PERMISSION</c> value: <c>"*": "deny"</c>, each forbidden tool denied,
    /// the read family, and one <c>{server}_*</c> entry per injected server. String values go through
    /// <c>(JsonNode?)</c> casts, which stay NativeAOT-safe (IL3050).</summary>
    internal static string Build(IReadOnlyList<AcpMcpServerSpec> injected) {
        var permission = new JsonObject { ["*"] = (JsonNode?)"deny" };

        // OpenCode resolves a specific key before the wildcard, so a file config's `bash: "allow"`
        // would otherwise survive `"*": "deny"`.
        foreach (var tool in OpenCodeForbiddenTools.Names)
            permission[tool] = (JsonNode?)"deny";

        foreach (var tool in ReadTools)
            permission[tool] = (JsonNode?)"allow";

        foreach (var server in injected) {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException(
                    "opencode_reviewer_permission_unnamed_server: an injected MCP server has no wire "
                  + "name, so its flattened tool names cannot be admitted. Refusing rather than "
                  + "launching a reviewer that cannot reach its own result channel.");

            // The name is interpolated into a glob, where syntax would widen the entry past this server.
            if (!server.Name.All(IsAdmissibleServerNameChar))
                throw new InvalidOperationException(
                    $"opencode_reviewer_permission_unsafe_server_name: injected MCP server "
                  + $"'{server.Name}' contains a character outside [A-Za-z0-9._-], so interpolating it "
                  + "into a permission glob could widen the reviewer's tool surface beyond that server. "
                  + "Refusing the launch.");

            permission[$"{server.Name}_*"] = (JsonNode?)"allow";
        }

        return permission.ToJsonString();
    }
}
