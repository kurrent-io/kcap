using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

public class CopilotWorkspaceTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task User_named_reads_as_a_rename() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: true\n");

        var title = new CopilotWorkspaceTitle(path).Read();

        await Assert.That(title!.Title).IsEqualTo("proj");
        await Assert.That(title.Kind).IsEqualTo(HarnessTitleKind.Rename);
    }

    [Test]
    public async Task Auto_generated_reads_as_auto() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: false\n");

        var title = new CopilotWorkspaceTitle(path).Read();

        await Assert.That(title!.Kind).IsEqualTo(HarnessTitleKind.Auto);
    }

    [Test]
    public async Task No_name_reads_null() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.CreateFile("workspace.yaml", "cwd: /work\n")).Read()).IsNull();
    }

    [Test]
    public async Task Missing_file_reads_null() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.PathTo("absent.yaml")).Read()).IsNull();
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.PathTo("absent.yaml")).RecordsChangeTime).IsFalse();
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: true\n");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(new CopilotWorkspaceTitle(path).Read()!.Title).IsEqualTo("proj");
    }

    const string Prompt = "Where does copper-lantern, the new receipt printer, keep its queue?";

    string SessionWithFirstPrompt(string workspaceYaml) {
        Tmp.CreateFile("events.jsonl",
            "{\"type\":\"session.start\",\"data\":{}}\n" +
            "{\"type\":\"user.message\",\"data\":{\"content\":\"" + Prompt + "\"}}\n");

        return Tmp.CreateFile("workspace.yaml", workspaceYaml);
    }

    /// <summary>Copilot names a session it never summarised with its first prompt, cut or whole: that is no title,
    /// so the server titles it from the prompt as it does any other harness's session.</summary>
    [Test]
    [Arguments("name: Where does copper-lantern, the new recei\nuser_named: false\n")]
    [Arguments("name: Where does copper-lantern, the new receipt...\nuser_named: false\n")]
    [Arguments("name: 'Where does copper-lantern, the new receipt printer, keep its queue?'\nuser_named: false\n")]
    public async Task An_auto_name_that_only_repeats_the_first_prompt_reads_null(string yaml) {
        await Assert.That(new CopilotWorkspaceTitle(SessionWithFirstPrompt(yaml)).Read()).IsNull();
    }

    [Test]
    public async Task A_generated_name_still_reads() {
        var title = new CopilotWorkspaceTitle(SessionWithFirstPrompt("name: Locate the receipt printer queue\nuser_named: false\n")).Read();

        await Assert.That(title!.Title).IsEqualTo("Locate the receipt printer queue");
    }

    [Test]
    public async Task A_user_name_that_repeats_the_first_prompt_still_reads() {
        var title = new CopilotWorkspaceTitle(SessionWithFirstPrompt("name: Where does copper-lantern\nuser_named: true\n")).Read();

        await Assert.That(title!.Kind).IsEqualTo(HarnessTitleKind.Rename);
    }
}
