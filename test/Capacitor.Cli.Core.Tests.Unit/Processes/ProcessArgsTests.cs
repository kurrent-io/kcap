using Capacitor.Cli.Core.Processes;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Processes;

public class ProcessArgsTests {
    /// The kernel pads the exec path with NULs before argv; the arguments are read past that padding.
    [Test]
    public async Task Parse_reads_the_arguments_after_the_exec_path_and_its_padding() {
        var plain  = ProcArgs2.Of(3, "/opt/homebrew/bin/node", "node", "/usr/local/bin/gemini", "--resume");
        var padded = ProcArgs2.Of(3, "/opt/homebrew/bin/node\0\0\0", "node", "/usr/local/bin/gemini", "--resume");

        await Assert.That(ProcessArgs.Parse(plain)).IsEquivalentTo(["node", "/usr/local/bin/gemini", "--resume"], CollectionOrdering.Matching);
        await Assert.That(ProcessArgs.Parse(padded)).IsEquivalentTo(["node", "/usr/local/bin/gemini", "--resume"], CollectionOrdering.Matching);
        await Assert.That(ProcessArgs.Parse(new byte[] { 1, 0, 0, 0 })).IsEmpty();
    }

    /// An empty argument is still one argument, so the count stops before the environment that follows.
    [Test]
    public async Task Parse_counts_an_empty_argument_and_stops_before_the_environment() {
        var buffer = ProcArgs2.Of(2, "/usr/bin/node", "node", "").Concat("gemini\0"u8.ToArray()).ToArray();

        await Assert.That(ProcessArgs.Parse(buffer)).IsEquivalentTo(["node", ""], CollectionOrdering.Matching);
    }

    /// Gemini CLI runs as node with no title, so only its script names it: the shim the shebang hands over,
    /// or the entry point inside its package. Neither another runtime's script, an option's value nor inline
    /// code is taken for that script.
    [Test]
    [Arguments(new[] { "node", "/usr/local/bin/gemini", "--yolo" }, "gemini")]
    [Arguments(new[] { "/usr/bin/node", "--no-warnings", "/usr/lib/node_modules/@google/gemini-cli/dist/index.js" }, "gemini-cli")]
    [Arguments(new[] { "C:\\Program Files\\nodejs\\node.exe", "C:\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js" }, "gemini-cli")]
    [Arguments(new[] { "/usr/bin/python3", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node" }, null)]
    [Arguments(new[] { "node", "--require", "/tmp/gemini.js", "/tmp/commit.js" }, null)]
    [Arguments(new[] { "node", "-e", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node", "--no-warnings", "--", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "bun", "run", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "node", "--import", "tsx", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "node", "/home/a/scripts/gemini.js" }, null)]
    [Arguments(new[] { "node", "--eval=1", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node", "-pe", "/usr/local/bin/gemini" }, null)]
    public async Task A_script_run_by_a_runtime_names_its_agent(string[] argv, string? expected) {
        var names = ProcessArgs.ScriptNameCandidates(argv).ToList();

        if (expected is not null) await Assert.That(names).Contains(expected);
        else await Assert.That(names.Any(name => AgentProcessNames.Matches(name, "gemini"))).IsFalse();
    }
}
