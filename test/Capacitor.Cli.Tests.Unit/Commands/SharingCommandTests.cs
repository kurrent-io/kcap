using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class SharingCommandTests {
    static async Task<(int Exit, string Out, string Err)> RunAsync(RecordingSharingApi sharing, params string[] args) {
        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new SharingCommand(sharing).HandleAsync(["sharing", .. args]);

        return (exit, console.GetCapturedOutput(), console.GetCapturedError());
    }

    /// <summary>The keys go to the server exactly as typed: nothing here knows what kind of object a key names.</summary>
    [Test]
    public async Task Set_gives_one_grant_by_the_keys_as_given() {
        var sharing = new RecordingSharingApi();

        var (exit, output, _) = await RunAsync(sharing, "set", "session:01J9", "team:platform", "--relation", "viewer");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sharing.Calls).IsEquivalentTo(new[] { "share:session:01J9:team:platform:viewer" });
        await Assert.That(output.Trim()).IsEqualTo("team:platform now holds viewer.");
    }

    [Test]
    public async Task Remove_takes_one_grant_away() {
        var sharing = new RecordingSharingApi();

        var (exit, _, _) = await RunAsync(sharing, "remove", "page:a1", "user:9");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sharing.Calls).IsEquivalentTo(new[] { "unshare:page:a1:user:9" });
    }

    [Test]
    public async Task Candidates_join_the_words_after_the_key_into_one_query() {
        var sharing = new RecordingSharingApi();

        await RunAsync(sharing, "candidates", "page:a1", "platform", "team");

        await Assert.That(sharing.Calls).IsEquivalentTo(new[] { "candidates:page:a1:platform team" });
    }

    [Test]
    [Arguments("set", "page:a1", "user:9")]
    [Arguments("set", "page:a1", "user:9", "--relation")]
    [Arguments("set", "page:a1", "--relation", "viewer")]
    [Arguments("set", "page:a1", "user:9", "--as", "viewer")]
    [Arguments("remove", "page:a1")]
    [Arguments("list")]
    [Arguments("grant", "page:a1")]
    public async Task A_malformed_command_is_a_usage_error_that_sends_nothing(params string[] args) {
        var sharing = new RecordingSharingApi();

        var (exit, _, error) = await RunAsync(sharing, args);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(sharing.Calls).IsEmpty();
        await Assert.That(error).Contains("kcap sharing set");
    }

    [Test]
    public async Task No_match_says_nobody_matched() {
        var (exit, output, _) = await RunAsync(new RecordingSharingApi(), "candidates", "page:a1", "platform");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output.Trim()).IsEqualTo("No candidates match \"platform\".");
    }

    [Test]
    public async Task A_refusal_prints_the_servers_message() {
        var sharing = new RecordingSharingApi { WriteResult = new SharingWriteResult.Refused(new("locked", "The owner keeps their own access.")) };

        var (exit, output, error) = await RunAsync(sharing, "remove", "page:a1", "user:7");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEmpty();
        await Assert.That(error.Trim()).IsEqualTo("The owner keeps their own access.");
    }

    [Test]
    public async Task A_key_nothing_answers_to_says_where_keys_come_from() {
        var sharing = new RecordingSharingApi { WriteResult = new SharingWriteResult.NotFound() };

        var (exit, _, error) = await RunAsync(sharing, "remove", "page:gone", "user:7");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error.Trim()).IsEqualTo(SharingCommand.NotVisibleMessage);
    }

    static readonly SharingAudienceDto Ada  = new("user:7", "Ada", "user", null, null, false);
    static readonly SharingRelationDto View = new("viewer", "Can view");

    [Test]
    public async Task A_viewer_is_shown_the_grants_and_why_they_cannot_change_them() {
        var lines = SharingCommand.GrantLines(new(false, "Only the owner shares this page.", [new(Ada, View, [], false)])).ToList();

        await Assert.That(lines).IsEquivalentTo(new[] {
            "AUDIENCE  KIND  RELATION           CAN MOVE TO  REMOVABLE  NAME",
            "user:7    user  Can view (viewer)  -            -          Ada",
            "You cannot change who this is shared with: Only the owner shares this page."
        });
    }

    [Test]
    public async Task A_manager_sees_what_each_grant_may_become() {
        var lines = SharingCommand.GrantLines(new(true, null, [new(Ada, View, [View, new("editor", "Can edit")], true)])).ToList();

        await Assert.That(lines).IsEquivalentTo(new[] {
            "AUDIENCE  KIND  RELATION           CAN MOVE TO                           REMOVABLE  NAME",
            "user:7    user  Can view (viewer)  Can view (viewer), Can edit (editor)  yes        Ada"
        });
    }

    [Test]
    public async Task Candidates_put_the_default_relation_first_and_show_an_unknown_kind_as_other() {
        var lines = SharingCommand.CandidateLines([
            new(new("group:x", "Ops", "guild", "12 members", null, true), [new("editor", "Can edit"), View], "viewer")
        ]).ToList();

        await Assert.That(lines[1]).IsEqualTo("group:x   other  Can view (viewer)*, Can edit (editor)  Ops (12 members) [inactive]");
    }

    [Test]
    public async Task Names_from_the_server_cannot_drive_the_terminal() {
        var lines = SharingCommand.GrantLines(new(true, null, [new(new("user:7", "Eve\u001b[2J\nRoot", "user", null, null, false), View, [], false)])).ToList();

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[1]).DoesNotContain("\u001b");
        await Assert.That(lines[1]).EndsWith("Eve\\u001b[2J Root");
    }

    sealed class RecordingSharingApi : ISharingApi {
        public List<string> Calls { get; } = [];

        public SharingWriteResult WriteResult { get; init; } = new SharingWriteResult.Applied();

        public Task<SharingRead<SharingDto>> GetAsync(string objectKey, CancellationToken ct = default) {
            Calls.Add($"get:{objectKey}");
            return Task.FromResult<SharingRead<SharingDto>>(new SharingRead<SharingDto>.Found(new(true, null, [])));
        }

        public Task<SharingRead<SharingCandidatesDto>> CandidatesAsync(string objectKey, string? query, CancellationToken ct = default) {
            Calls.Add($"candidates:{objectKey}:{query}");
            return Task.FromResult<SharingRead<SharingCandidatesDto>>(new SharingRead<SharingCandidatesDto>.Found(new([])));
        }

        public Task<SharingWriteResult> ShareAsync(string objectKey, string audienceKey, string relation, CancellationToken ct = default) {
            Calls.Add($"share:{objectKey}:{audienceKey}:{relation}");
            return Task.FromResult(WriteResult);
        }

        public Task<SharingWriteResult> UnshareAsync(string objectKey, string audienceKey, CancellationToken ct = default) {
            Calls.Add($"unshare:{objectKey}:{audienceKey}");
            return Task.FromResult(WriteResult);
        }
    }
}
