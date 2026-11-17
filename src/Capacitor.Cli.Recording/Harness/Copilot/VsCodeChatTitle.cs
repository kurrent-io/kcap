using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>A VS Code Local session's <c>customTitle</c>. VS Code writes its generated title and a user's rename to the
/// same field, so every title reads as generated.</summary>
public sealed class VsCodeChatTitle(VsCodeChatSessionCache log) : IHarnessTitleStore {
    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        try {
            return log.Current()?.CustomTitle is { } title ? new StoreTitle(title, HarnessTitleKind.Auto, null) : null;
        } catch (Exception) {
            return null;
        }
    }

    public static VsCodeChatTitle? ForTranscript(string transcriptPath) =>
        CopilotPaths.ChatSessionLogFor(transcriptPath) is { } logPath
            ? new(new VsCodeChatSessionCache(logPath, Path.GetFileNameWithoutExtension(transcriptPath), _ => { }))
            : null;
}
