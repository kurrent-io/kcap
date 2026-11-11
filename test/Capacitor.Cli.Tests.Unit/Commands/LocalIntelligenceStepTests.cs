using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Setup's background intelligence step: a flag answers it, a person is asked until the server takes an answer
/// or they choose not now, and a run that can do neither leaves it unset and says how to set it later</summary>
public class LocalIntelligenceStepTests {
    const string Installation = "0123456789abcdef0123456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly IntelligenceSetupResponse Took =
        new("local_agent", "claude", null, true, 200, ["titles", "outcomes"]);

    sealed class Script(params string[] choices) : IIntelligenceQuestions {
        readonly Queue<string> _choices = new(choices);

        public List<IReadOnlyList<string>> Offered { get; } = [];
        public string? TypedKey { get; init; } = "typed";
        public bool Outcomes { get; init; } = true;
        public int OutcomeQuestions { get; private set; }

        public string Choose(IReadOnlyList<string> harnesses) {
            Offered.Add(harnesses);

            return _choices.Dequeue();
        }

        public IntelligenceVendor Vendor() {
            var id = _choices.Dequeue();

            return IntelligenceVendor.All.Single(v => v.Id == id);
        }
        public string Model(IntelligenceVendor vendor) => _choices.Dequeue();
        public string? Key(IntelligenceVendor vendor) => TypedKey;

        public bool ImportOutcomes() {
            OutcomeQuestions++;

            return Outcomes;
        }
    }

    sealed class Server(params IntelligenceSetupOutcome[] outcomes) {
        readonly Queue<IntelligenceSetupOutcome> _outcomes = new(outcomes);

        public List<IntelligenceSetupRequest> Requests { get; } = [];

        public Task<IntelligenceSetupOutcome> Apply(IntelligenceSetupRequest request) {
            Requests.Add(request);

            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    static LocalSetupArgs Args(string? answer = null, string? model = null, bool? outcomes = null) =>
        new(false, true, true, answer is not null, Intelligence: IntelligenceAnswer.Parse(answer), IntelligenceModel: model, ImportOutcomes: outcomes);

    (LocalIntelligenceStep Step, List<string> Said) Step(Server server, IIntelligenceQuestions? questions, Func<string, string?>? env = null) {
        var said = new List<string>();

        return (new LocalIntelligenceStep(server.Apply, () => Task.FromResult<string[]>(["claude", "codex"]), env ?? (_ => null), questions, said.Add, Config.Root, Installation), said);
    }

    [Test]
    public async Task A_flag_answer_is_posted_without_asking() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var (step, _) = Step(server, null);

        var applied = await step.RunAsync(Args("claude", outcomes: false));

        await Assert.That(applied).IsEqualTo(Took);
        await Assert.That(server.Requests.Single()).IsEqualTo(new IntelligenceSetupRequest("local_agent", "claude", ImportOutcomes: false));
    }

    [Test]
    public async Task A_flag_provider_takes_its_key_from_the_environment() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var (step, _) = Step(server, null, name => name == "ANTHROPIC_API_KEY" ? "sk-env" : null);

        await step.RunAsync(Args("anthropic", model: "claude-haiku-4-5"));

        await Assert.That(server.Requests.Single()).IsEqualTo(
            new IntelligenceSetupRequest("provider", null, "anthropic", "claude-haiku-4-5", "sk-env"));
    }

    /// Setup's none is not now, so an unattended re-run keeps an earlier answer; kcap local intelligence none turns it off.
    [Test]
    public async Task A_flag_none_posts_nothing() {
        var server = new Server();
        var (step, said) = Step(server, null);

        await Assert.That(await step.RunAsync(Args(IntelligenceAnswer.NoneToken))).IsNull();
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(string.Join('\n', said)).Contains("kcap local intelligence");
    }

    [Test]
    public async Task A_refused_flag_answer_is_reported_and_setup_carries_on() {
        var server = new Server(new IntelligenceSetupOutcome.Refused("key_rejected: 401"));
        var (step, said) = Step(server, null, _ => "sk");

        var applied = await step.RunAsync(Args("openai"));

        await Assert.That(applied).IsNull();
        await Assert.That(string.Join('\n', said)).Contains("The provider refused the key: 401");
        await Assert.That(string.Join('\n', said)).Contains("kcap local intelligence");
    }

    [Test]
    public async Task Nobody_to_ask_and_no_flag_leaves_it_unset() {
        var server = new Server();
        var (step, said) = Step(server, null);

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(string.Join('\n', said)).Contains("kcap local intelligence");
    }

    /// <summary>With nobody to ask, a new installation is told it runs nothing and an existing one that it is left as it
    /// was; both name the real answers rather than a placeholder.</summary>
    [Test]
    [Arguments(false, "Background intelligence: off")]
    [Arguments(true, "Background intelligence: left as it was.")]
    public async Task Nobody_to_ask_says_what_stands_and_names_the_answers(bool existing, string expected) {
        var server = new Server();
        var (step, said) = Step(server, null);

        await step.RunAsync(Args(), existing);

        var text = string.Join('\n', said);
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(text).Contains(expected);
        await Assert.That(text).Contains("kcap local intelligence with one of: claude, codex");
        await Assert.That(text).DoesNotContain("<answer>");
    }

    [Test]
    public async Task Asked_the_local_agent_then_outcomes() {
        var server  = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var script  = new Script("codex") { Outcomes = false };
        var (step, _) = Step(server, script);

        await step.RunAsync(Args());

        await Assert.That(script.Offered.Single()).IsEquivalentTo(["claude", "codex"]);
        await Assert.That(server.Requests.Single()).IsEqualTo(new IntelligenceSetupRequest("local_agent", "codex", ImportOutcomes: false));
    }

    [Test]
    public async Task Not_now_posts_nothing_and_asks_nothing_more() {
        var server = new Server();
        var script = new Script(IntelligenceAnswer.NoneToken);
        var (step, _) = Step(server, script);

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(script.OutcomeQuestions).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_provider_is_asked_again_until_one_is_taken() {
        var server = new Server(new IntelligenceSetupOutcome.Refused("key_rejected: 401"), new IntelligenceSetupOutcome.Applied(Took));
        var script = new Script(LocalIntelligenceStep.ProviderChoice, "cohere", "command-r", "claude");
        var (step, said) = Step(server, script);

        var applied = await step.RunAsync(Args());

        await Assert.That(applied).IsEqualTo(Took);
        await Assert.That(server.Requests[0]).IsEqualTo(new IntelligenceSetupRequest("provider", null, "cohere", "command-r", "typed", true));
        await Assert.That(server.Requests[1].Harness).IsEqualTo("claude");
        await Assert.That(string.Join('\n', said)).Contains("The provider refused the key: 401");
    }

    [Test]
    public async Task An_outcome_flag_is_not_asked_again() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var script = new Script("claude");
        var (step, _) = Step(server, script);

        await step.RunAsync(Args(outcomes: true) with { Intelligence = null });

        await Assert.That(script.OutcomeQuestions).IsEqualTo(0);
        await Assert.That(server.Requests.Single().ImportOutcomes == true).IsTrue();
    }

    [Test]
    public async Task An_unreachable_server_warns_and_moves_on() {
        var server = new Server(new IntelligenceSetupOutcome.Failed("connection refused"));
        var (step, said) = Step(server, new Script("claude"));

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(string.Join('\n', said)).Contains("connection refused");
    }

    [Test]
    public async Task An_applied_answer_is_recorded_for_the_installation() {
        var (step, _) = Step(new Server(new IntelligenceSetupOutcome.Applied(Took)), null);

        await step.RunAsync(Args("claude"));

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, Installation)).IsEqualTo(new LocalIntelligenceRecord(Installation, "local_agent"));
    }

    /// <summary>An answer the server took but this CLI could not record leaves its model calls off, so the person is told.</summary>
    [Test]
    public async Task An_applied_answer_that_cannot_be_recorded_says_so() {
        Directory.CreateDirectory(Config.Root.Path(LocalIntelligenceRecord.FileName));
        var (step, said) = Step(new Server(new IntelligenceSetupOutcome.Applied(Took)), null);

        await step.RunAsync(Args("claude"));

        await Assert.That(said).Contains(LocalIntelligenceCommand.RecordNotSavedLine);
    }

    /// <summary>Not now posts nothing and a refused answer applies nothing, so the mode an earlier run recorded stands.</summary>
    [Test]
    [Arguments(IntelligenceAnswer.NoneToken)]
    [Arguments("openai")]
    public async Task An_answer_the_server_did_not_apply_keeps_the_recorded_mode(string answer) {
        new LocalIntelligenceRecord(Installation, "provider").Save(Config.Root);
        var (step, _) = Step(new Server(new IntelligenceSetupOutcome.Refused("key_rejected: 401")), null, _ => "sk");

        await step.RunAsync(Args(answer));

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, Installation).Mode).IsEqualTo("provider");
    }
}
