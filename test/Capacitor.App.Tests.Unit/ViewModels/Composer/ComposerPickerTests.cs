using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels.Composer;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels.Composer;

public class ComposerPickerTests {
    static readonly ComposerContext Repo = new("/repo", HarnessId.Claude);

    static ComposerPicker Picker(
            IReadOnlyList<HostedAgentCommand>? commands = null, IReadOnlyList<string>? repo = null,
            Func<string, IReadOnlyList<string>>? folder = null, List<string>? reads = null, ComposerContext? context = null) =>
        new(Observable.Return<ComposerContext?>(context ?? Repo),
            new CommandCompletion(_ => Task.FromResult(commands ?? []), harness => harness == HarnessId.Codex ? '$' : '/'),
            new MentionCompletion(
                _ => {
                    reads?.Add("repo");
                    return Task.FromResult(repo ?? []);
                },
                (_, f) => {
                    reads?.Add(f);
                    return Task.FromResult(folder?.Invoke(f) ?? []);
                }));

    [Test]
    public async Task Commands_rank_prefix_then_segment_then_contains_then_description() {
        HostedAgentCommand[] commands = [
            new("unrelated", "nothing", null),
            new("described", "runs a review", null),
            new("prereview", null, null),
            new("pr:review", null, null),
            new("review", null, null),
        ];

        var names = CommandCompletion.Rank(commands, "review").Select(c => c.Name);

        await Assert.That(names).IsEquivalentTo(["review", "pr:review", "prereview", "described"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Repo_files_rank_by_file_name_before_path_and_shorter_first() {
        string[] paths = ["src/deep/nested/Picker.cs", "src/Picker.cs", "docs/MyPicker.md", "picker/readme.md", "other.txt"];

        var ranked = MentionCompletion.RankRepo(paths, "picker").Select(m => m.Path);

        await Assert.That(ranked).IsEquivalentTo(
            ["src/Picker.cs", "src/deep/nested/Picker.cs", "docs/MyPicker.md", "picker/readme.md"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Folder_entries_hide_dotfiles_until_the_part_starts_with_a_dot() {
        string[] names = ["src/", ".git/", ".env", "readme.md"];

        await Assert.That(MentionCompletion.RankFolder(names, "./", "").Select(m => m.Path))
            .IsEquivalentTo(["./src/", "./readme.md"], CollectionOrdering.Matching);
        await Assert.That(MentionCompletion.RankFolder(names, "./", ".e").Select(m => m.Path))
            .IsEquivalentTo(["./.env"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_file_replaces_its_query_and_a_folder_keeps_the_query_open() {
        await Assert.That(ComposerPicker.Insert(new FileMention("src/a.cs"), "see @a. now", 7))
            .IsEqualTo(("see @src/a.cs  now", 14));
        await Assert.That(ComposerPicker.Insert(new FileMention("my dir/a b.cs"), "@a", 2))
            .IsEqualTo(("@\"my dir/a b.cs\" ", 17));
        await Assert.That(ComposerPicker.Insert(new FileMention("./src/"), "@./s", 4))
            .IsEqualTo(("@./src/", 7));
        await Assert.That(ComposerPicker.Insert(new FileMention("./my dir/"), "@./m", 4))
            .IsEqualTo(("@\"./my dir/", 11));
        await Assert.That(ComposerPicker.Insert(new CommandRow(new("review", null, null), '/'), "/rev", 4))
            .IsEqualTo(("/review ", 8));
    }

    [Test]
    public async Task A_listing_is_read_once_per_query_and_again_after_it_ends() {
        var reads = new List<string>();
        var picker = Picker(repo: ["src/a.cs"], folder: _ => ["b.cs"], reads: reads);

        await picker.UpdateAsync("@a", 2);
        await picker.UpdateAsync("@a.", 3);
        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new FileMention("src/a.cs")]);
        await picker.UpdateAsync("@a. ", 4);
        await Assert.That(picker.Rows).IsEmpty();
        await picker.UpdateAsync("@./", 3);
        await picker.UpdateAsync("@./b", 4);
        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new FileMention("./b.cs")]);
        await picker.UpdateAsync("@a", 2);

        await Assert.That(reads).IsEquivalentTo(["repo", "./", "repo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_quoted_folder_query_lists_that_folder() {
        var reads = new List<string>();
        var picker = Picker(folder: _ => ["a b.cs"], reads: reads);

        await picker.UpdateAsync("@\"./my dir/a", 12);

        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new FileMention("./my dir/a b.cs")]);
        await Assert.That(reads).IsEquivalentTo(["./my dir/"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Each_new_context_lists_its_commands_up_front_and_reads_its_own_files() {
        var context = new BehaviorSubject<ComposerContext?>(new("/a", HarnessId.Claude));
        var reads = new List<string>();
        using var picker = new ComposerPicker(context,
            new CommandCompletion(c => {
                reads.Add($"commands {c.Directory}");
                return Task.FromResult<IReadOnlyList<HostedAgentCommand>>([]);
            }, _ => '/'),
            new MentionCompletion(
                dir => {
                    reads.Add($"files {dir}");
                    return Task.FromResult<IReadOnlyList<string>>([]);
                },
                (_, _) => Task.FromResult<IReadOnlyList<string>>([])));

        await picker.UpdateAsync("@a", 2);
        context.OnNext(new("/b", HarnessId.Claude));
        await picker.UpdateAsync("@ab", 3);

        await Assert.That(reads).IsEquivalentTo(["commands /a", "files /a", "commands /b", "files /b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_slash_opens_commands_only_as_the_whole_text() {
        var picker = Picker(commands: [new("review", null, null)]);

        await picker.UpdateAsync("/re", 3);
        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new CommandRow(new("review", null, null), '/')]);
        await picker.UpdateAsync("say /re", 7);
        await Assert.That(picker.Rows).IsEmpty();
    }

    [Test]
    public async Task A_codex_skill_opens_on_slash_or_dollar_and_goes_in_as_dollar() {
        var picker = Picker(commands: [new("review", null, null)], context: new("/repo", HarnessId.Codex));

        await picker.UpdateAsync("$re", 3);
        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new CommandRow(new("review", null, null), '$')]);
        await picker.UpdateAsync("/re", 3);
        await Assert.That(picker.Rows).IsEquivalentTo<IReadOnlyList<object>, object>([new CommandRow(new("review", null, null), '$')]);
        await Assert.That(ComposerPicker.Insert(picker.Rows[0], "/re", 3)).IsEqualTo(("$review ", 8));
    }

    [Test]
    public async Task A_dollar_opens_nothing_for_claude() {
        var picker = Picker(commands: [new("review", null, null)]);

        await picker.UpdateAsync("$re", 3);

        await Assert.That(picker.Rows).IsEmpty();
    }
}
