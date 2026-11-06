using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Recording.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Kiro;

namespace Capacitor.Cli.Recording.Harness.Titles;

/// <summary>The title store a session watcher polls, located from the transcript it watches. Null for a subagent
/// watcher, and for a vendor whose title is carried inline in the transcript instead.</summary>
public static class HarnessTitleStores {
    public static IHarnessTitleStore? For(string vendor, string? agentId, string sessionId, string transcriptPath, HarnessRegistry harnesses,
            IRecorderHost host, AccountRegistry? accounts = null, UserHome? home = null) {
        if (agentId is not null) return null;

        return vendor switch {
            "codex"       => new CodexSessionIndexTitle(CodexHome(transcriptPath, harnesses, accounts, home), sessionId),
            "copilot"     => new CopilotWorkspaceTitle(Path.Combine(Path.GetDirectoryName(transcriptPath)!, "workspace.yaml")),
            "kiro"        => new KiroSessionTitle(Path.ChangeExtension(transcriptPath, ".json")),
            "cursor"      => Cursor(harnesses.Of<CursorHarness>().Paths, transcriptPath, host.OpenSqliteTitleStore(vendor, transcriptPath)),
            "antigravity" => host.OpenSqliteTitleStore(vendor, transcriptPath),
            _             => null,
        };
    }

    // The account that wrote the rollout owns its index; the environment layout is only the fallback.
    static string CodexHome(string rolloutPath, HarnessRegistry harnesses, AccountRegistry? accounts, UserHome? home) =>
        accounts is not null && home is not null && AccountPaths.CodexForRollout(rolloutPath, accounts, home) is { } paths
            ? paths.Home
            : harnesses.Of<CodexHarness>().Paths.Home;

    // The CLI's per-chat meta.json first; the IDE's composer record otherwise.
    public static IHarnessTitleStore Cursor(CursorPaths paths, string transcriptPath, IHarnessTitleStore? composer) {
        var chat = CursorChatTitle.ForTranscript(paths.ChatsDir, transcriptPath);

        return composer is not null
            ? new FirstTitleStore(chat, composer)
            : chat;
    }
}
