using System.Globalization;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

public class CopilotWorkspaceYamlTests {
    [Test]
    public async Task workspace_yaml_parser_tolerates_missing_file_and_colon_values() {
        using var tmp = new TempDir();

        await Assert.That(CopilotWorkspaceYaml.TryRead(tmp.PathTo("absent.yaml"))).IsNull();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: 4ae28a73-dd66-46ac-81d2-be94b5e87079
            cwd: /private/tmp/work
            name: Fix the bug: timestamps are wrong
            user_named: false
            created_at: 2026-06-10T20:23:25.556Z
            updated_at: 2026-06-10T20:23:37.838Z
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Cwd).IsEqualTo("/private/tmp/work");
        // Values containing ": " must not be truncated at the second colon.
        await Assert.That(meta.Name).IsEqualTo("Fix the bug: timestamps are wrong");
        await Assert.That(meta.UserNamed).IsFalse();
        await Assert.That(meta.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z", CultureInfo.InvariantCulture));
        await Assert.That(meta.UpdatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:37.838Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task workspace_yaml_parser_strips_scalar_quotes_from_names() {
        // Copilot quotes the name when it contains YAML-special sequences —
        // the captured fixture has `name: 'Reply with exactly: ok'`. Without
        // unquoting, the literal quotes leak into imported session titles.
        using var tmp = new TempDir();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: e18d0fe8-95e1-4bd3-87e1-54318e4f3c54
            cwd: '/private/tmp/quoted dir'
            name: 'Reply with exactly: ok'
            created_at: '2026-06-10T20:23:21.000Z'
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Name).IsEqualTo("Reply with exactly: ok");
        await Assert.That(meta.Cwd).IsEqualTo("/private/tmp/quoted dir");
        await Assert.That(meta.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:21.000Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task workspace_yaml_parser_reads_the_repository_and_branch_copilot_recorded() {
        using var tmp = new TempDir();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: 9157a15c-8a35-40ef-b3de-e35a5ef956a2
            cwd: /work/widget
            git_root: /work/widget
            repository: acme/widget
            host_type: github
            branch: feature/x
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta!.Repository).IsEqualTo("acme/widget");
        await Assert.That(meta.Branch).IsEqualTo("feature/x");
    }

    [Test]
    [Arguments("'Reply with exactly: ok'", "Reply with exactly: ok")]
    [Arguments("'It''s done: finally'", "It's done: finally")]
    [Arguments("\"Fix the \\\"bug\\\": now\"", "Fix the \"bug\": now")]
    [Arguments("plain value", "plain value")]
    [Arguments("'unterminated", "'unterminated")]
    [Arguments("''", "")]
    public async Task unquote_handles_yaml_scalar_quote_forms(string raw, string expected) {
        await Assert.That(CopilotWorkspaceYaml.Unquote(raw)).IsEqualTo(expected);
    }

    [Test]
    public async Task workspace_yaml_parser_reads_a_block_scalar_name() {
        using var tmp = new TempDir();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, "cwd: /work\nname: |-\n  Review this PR.\n\n  Additional instructions:\n    (none)\nuser_named: false\n");

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta!.Name).IsEqualTo("Review this PR.\n\nAdditional instructions:\n  (none)");
        await Assert.That(meta.UserNamed).IsFalse();
    }

    /// <summary>A folded scalar joins a paragraph's lines with spaces, keeps a blank line as one break, and keeps
    /// the breaks around a more-indented line.</summary>
    [Test]
    [Arguments("  Release\n  notes\n\n  Migration steps\n", "Release notes\nMigration steps")]
    [Arguments("  Steps:\n    one\n  done\n", "Steps:\n  one\ndone")]
    public async Task workspace_yaml_parser_folds_a_folded_scalar_name(string block, string expected) {
        using var tmp = new TempDir();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, $"name: >-\n{block}user_named: true\n");

        await Assert.That(CopilotWorkspaceYaml.TryRead(path)!.Name).IsEqualTo(expected);
    }
}
