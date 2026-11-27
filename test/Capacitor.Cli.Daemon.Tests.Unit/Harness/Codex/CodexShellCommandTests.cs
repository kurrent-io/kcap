using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// An app-server command line reads as the script it runs. Codex joins the argv it spawns into one shell-quoted line,
/// so only a line that splits back into exactly <c>&lt;shell&gt; -c|-lc &lt;script&gt;</c> is peeled; a line the joiner
/// would not have written is shown as it came, never as a guess at what runs.
/// </summary>
public class CodexShellCommandTests {
    [Test]
    [Arguments("/bin/bash -lc 'touch kcap-check && echo kcap-check-ran'", "touch kcap-check && echo kcap-check-ran")]
    [Arguments("/bin/zsh -lc 'git status'", "git status")]
    [Arguments("bash -c 'ls -la'", "ls -la")]
    [Arguments("/bin/sh -c ls", "ls")]
    [Arguments("/bin/bash -lc 'echo it'\"'\"'s'", "echo it's")]
    [Arguments("/bin/bash -lc \"echo it's \\$HOME \\\"quoted\\\" \\\\ \\`tick\\`\"", "echo it's $HOME \"quoted\" \\ `tick`")]
    [Arguments("/bin/bash -lc 'printf a\nrm -rf b'", "printf a\nrm -rf b")]
    public async Task A_shell_wrapper_reads_as_its_script(string command, string script) =>
        await Assert.That(CodexShellCommand.Script(command)).IsEqualTo(script);

    [Test]
    [Arguments("git status")]
    [Arguments("/bin/bash -lc 'echo x' extra")]
    [Arguments("/bin/bash -lc 'echo x'>out")]
    [Arguments("/bin/bash -lc 'echo x' && rm -rf y")]
    [Arguments("/bin/bash -lc 'echo x';rm -rf y")]
    [Arguments("python3 -c 'print(1)'")]
    [Arguments("/bin/bash -x 'echo x'")]
    [Arguments("/bin/bash -lc 'unterminated")]
    [Arguments("/bin/bash -lc \"echo $HOME\"")]
    [Arguments("/bin/bash -lc \"echo \\n\"")]
    [Arguments("/bin/bash -lc ''")]
    [Arguments("/bin/bash\t-lc 'echo x'")]
    [Arguments("~/bin/bash -lc 'echo x'")]
    public async Task Anything_else_reads_as_it_came(string command) =>
        await Assert.That(CodexShellCommand.Script(command)).IsEqualTo(command);
}
