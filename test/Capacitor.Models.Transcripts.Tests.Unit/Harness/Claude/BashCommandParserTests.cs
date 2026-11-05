using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class BashCommandParserTests {
    [Test]
    public async Task Parse_Null_ReturnsNone() {
        var r = BashCommandParser.Parse(null);

        await Assert.That(r.Kind).IsEqualTo(BashCommandParser.BashKind.None);
        await Assert.That(r.RemainingText).IsEqualTo("");
    }

    [Test]
    public async Task Parse_Input_ExtractsCommand() {
        var r = BashCommandParser.Parse("<bash-input>kcap setup</bash-input>");

        await Assert.That(r.Kind).IsEqualTo(BashCommandParser.BashKind.Input);
        await Assert.That(r.Command).IsEqualTo("kcap setup");
        await Assert.That(r.RemainingText).IsEqualTo("");
    }

    [Test]
    public async Task Parse_Output_CombinesStdoutAndStderr() {
        var r = BashCommandParser.Parse("<bash-stdout></bash-stdout><bash-stderr>Welcome to Capacitor</bash-stderr>");

        await Assert.That(r.Kind).IsEqualTo(BashCommandParser.BashKind.Output);
        await Assert.That(r.OutputText).Contains("Welcome to Capacitor");
        await Assert.That(r.RemainingText).IsEqualTo("");
    }

    [Test]
    public async Task Parse_Stdout_WithoutClosingTag_CapturesToEnd() {
        var r = BashCommandParser.Parse("<bash-stdout>partial output no close");

        await Assert.That(r.Kind).IsEqualTo(BashCommandParser.BashKind.Output);
        await Assert.That(r.OutputText).Contains("partial output no close");
    }

    [Test]
    public async Task IsPure_PureInput_IsTrue() {
        await Assert.That(BashCommandParser.IsPure("<bash-input>kcap setup</bash-input>", out var r)).IsTrue();
        await Assert.That(r.Kind).IsEqualTo(BashCommandParser.BashKind.Input);
    }

    [Test]
    public async Task IsPure_PureOutput_IsTrue() {
        await Assert.That(BashCommandParser.IsPure("<bash-stdout>done</bash-stdout>", out _)).IsTrue();
    }

    [Test]
    public async Task IsPure_InputWithoutClose_IsFalse() {
        // bash-input is STRICT: no closing tag → not parsed as Input → stays a normal prompt.
        await Assert.That(BashCommandParser.IsPure("<bash-input> handling improvements", out _)).IsFalse();
    }

    [Test]
    public async Task IsPure_PromptQuotingInput_IsFalse() {
        await Assert.That(BashCommandParser.IsPure("Why is <bash-input>x</bash-input> in my transcript?", out _)).IsFalse();
    }

    [Test]
    public async Task IsPure_PlainPromptAndNull_AreFalse() {
        await Assert.That(BashCommandParser.IsPure("fix the login bug", out _)).IsFalse();
        await Assert.That(BashCommandParser.IsPure(null, out _)).IsFalse();
    }

    [Test]
    public async Task DisplayLabel_Input_PrefixesDollar() {
        BashCommandParser.IsPure("<bash-input>kcap setup</bash-input>", out var r);

        await Assert.That(BashCommandParser.DisplayLabel(r)).IsEqualTo("$ kcap setup");
    }

    [Test]
    public async Task DisplayLabel_Output_FirstMeaningfulLine() {
        BashCommandParser.IsPure("<bash-stdout></bash-stdout><bash-stderr>Welcome to Capacitor\nStep 2/5</bash-stderr>", out var r);

        await Assert.That(BashCommandParser.DisplayLabel(r)).IsEqualTo("Welcome to Capacitor");
    }

    [Test]
    public async Task DisplayLabel_Output_AllGarbage_IsPlaceholder() {
        BashCommandParser.IsPure($"<bash-stdout></bash-stdout><bash-stderr>{new string('�', 20)}</bash-stderr>", out var r);

        await Assert.That(BashCommandParser.DisplayLabel(r)).IsEqualTo("Command output");
    }

    [Test]
    public async Task DisplayLabel_Output_CappedAt200Chars() {
        BashCommandParser.IsPure($"<bash-stdout>{new string('x', 500)}</bash-stdout>", out var r);

        var label = BashCommandParser.DisplayLabel(r);

        await Assert.That(label.Length).IsEqualTo(201); // 200 + '…'
        await Assert.That(label).EndsWith("…");
    }
}
