using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary><c>opencode run</c> wraps every argument containing a space in quotes before sending it as the
/// prompt, so the plugin records a one-argument prompt as the user typed it. Asserted as text: the plugin
/// is generated TypeScript with no runtime in this suite.</summary>
public class OpenCodeRunPromptQuotingTests {
    const string Content = OpenCodeExtensionInstaller.ExtensionContent;

    static string Section(string from, string to) {
        var start = Content.IndexOf(from, StringComparison.Ordinal);
        var end   = start < 0 ? -1 : Content.IndexOf(to, start + from.Length, StringComparison.Ordinal);

        return end > start ? Content[start..end] : "";
    }

    /// <summary>The stored form is rebuilt exactly as OpenCode's run handler quotes an argument, and only from
    /// the arguments after <c>run</c>: a TUI or an attached server has none, so it rewrites nothing.</summary>
    [Test]
    public async Task The_stored_form_is_rebuilt_from_the_run_arguments_as_opencode_quotes_them() {
        var fn = Section("function quotedRunArgs()", "const runArgs = quotedRunArgs()");

        await Assert.That(fn).Contains("const at = argv.indexOf(\"run\")");
        await Assert.That(fn).Contains("if (at < 0) return []");
        await Assert.That(fn).Contains("argv.slice(at + 1)");
        await Assert.That(fn).Contains("a.includes(\" \")");
        await Assert.That(fn).Contains("""'"' + a.replace(/"/g, '\\"') + '"', a""");
        await Assert.That(fn).Contains("} catch {");
    }

    /// <summary>Only a text that is one whole quoted argument, alone or ahead of the piped stdin run appends
    /// after a newline, is unwrapped; a quoted phrase inside a longer message is left as stored.</summary>
    [Test]
    public async Task Only_a_whole_quoted_argument_is_unwrapped() {
        var fn = Section("function unquoteRunPrompt(", "function recordedParts(");

        await Assert.That(fn).Contains("if (text === stored) return typed");
        await Assert.That(fn).Contains("if (text.startsWith(stored + \"\\n\")) return typed + text.slice(stored.length)");
        await Assert.That(fn).Contains("return text");
        await Assert.That(fn).DoesNotContain("replace(");
    }

    /// <summary>The rewrite touches a user message's own text parts only, never an assistant reply or a
    /// synthetic part, and copies the part rather than mutating the SDK's object.</summary>
    [Test]
    public async Task Only_user_text_parts_are_rewritten_and_on_a_copy() {
        var fn = Section("function recordedParts(", "function writeMessages(");

        await Assert.That(fn).Contains("m?.info?.role !== \"user\"");
        await Assert.That(fn).Contains("p?.type !== \"text\" || p?.synthetic === true");
        await Assert.That(fn).Contains("{ ...p, text }");
        await Assert.That(fn).DoesNotContain("p.text =");
    }

    /// <summary>Every transcript line goes out through the rewrite, and the line keeps the message's own
    /// info and part ids, which are what the server dedups by.</summary>
    [Test]
    public async Task Every_written_line_carries_the_rewritten_parts() {
        await Assert.That(Content).Contains("JSON.stringify({ info: m.info, parts: recordedParts(m) })");
        await Assert.That(Content).DoesNotContain("parts: m.parts ?? []");
    }
}
