using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class TitleGeneratorTests {
    [Test]
    public async Task IsKnownCapacitorPrompt_detects_title_prompt() {
        var sample = TitleGeneration.TitlePromptPrefix + "trailing content";

        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt(sample)).IsTrue();
    }

    [Test]
    public async Task IsKnownCapacitorPrompt_detects_whats_done_prompt() {
        var sample = TitleGenerator.WhatsDonePromptPrefix + "\ntrailing content";

        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt(sample)).IsTrue();
    }

    [Test]
    public async Task IsKnownCapacitorPrompt_rejects_unrelated_text() {
        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt("hello world")).IsFalse();
    }

    const string RecommendedPlugins =
        "<recommended_plugins>\nThese plugins can help with this task:\n- github: work with pull requests\n</recommended_plugins>";

    static string UserLine(string text) =>
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":"
      + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

    [Test]
    public async Task ExtractCodexTitleContext_skips_a_recommended_plugins_message() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("rollout.jsonl", [UserLine(RecommendedPlugins), UserLine("What does the src folder hold?")]);

        var (userText, _) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsEqualTo("What does the src folder hold?");
    }

    [Test]
    public async Task ExtractCodexTitleContext_takes_the_prompt_after_the_plugin_block_in_the_same_message() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("rollout.jsonl", [UserLine(RecommendedPlugins + "\nWhat does the src folder hold?")]);

        var (userText, _) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsEqualTo("What does the src folder hold?");
    }
}
