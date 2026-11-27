using Capacitor.Cli.Core.Eval.RepoContext;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.RepoContext;

/// <summary>A local eval finds the session's repository among this machine's checkouts and reads its instructions there.</summary>
[ParallelLimiter<SubprocessLimit>]
public class RepoEvalContextSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Session = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    static readonly string Hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

    string Clone() {
        var upstream = GitRepo.InitIn(Tmp.CreateDir("upstream"));
        upstream.CreateFile("CLAUDE.md", "rules\n");
        upstream.CommitAll("seed");
        return upstream.Clone(Tmp.PathTo("clone")).Path;
    }

    static WireMockServer Summary(string repoHash) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/sessions/{Session}/summary").UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithBody($$"""{"session_id":"{{Session}}","repositories":[{"repo_hash":"{{repoHash}}","owner":"acme","repo_name":"widgets","is_primary":true}]}"""));
        server.Given(Request.Create().WithPath("/api/repos/eval-memories").UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithBody("""{"memories":"<<<repository-memories\n### note\nshared\nrepository-memories>>>\n\n"}"""));
        return server;
    }

    static async Task<string> BlockAsync(WireMockServer server, params string[] known) {
        var checkouts = new LocalRepoCheckouts(() => Task.FromResult(known), _ => "https://github.com/acme/widgets.git");
        using var client = new HttpClient();
        return await new RepoEvalContextSource(checkouts, TimeProvider.System).ForSessionAsync(client, server.Url!, Session, new RecordingEvalObserver(), CancellationToken.None);
    }

    [Test]
    public async Task The_checkout_of_the_sessions_primary_repository_supplies_the_block() {
        using var server = Summary(Hash);

        var block = await BlockAsync(server, Clone());

        await Assert.That(block).Contains("### CLAUDE.md\nrules\n");
        await Assert.That(block).EndsWith("repository-instructions>>>\n\n<<<repository-memories\n### note\nshared\nrepository-memories>>>\n\n");
    }

    [Test]
    public async Task No_checkout_of_the_repository_leaves_the_block_out() {
        using var server = Summary("0123456789abcdef");

        await Assert.That(await BlockAsync(server, Clone())).IsEmpty();
    }
}
