using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Channels;
using Capacitor.App.Services.Voice;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class VoiceAssistantTests {
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeMicrophone : IMicrophone {
        readonly TaskCompletionSource<Action<float[]>> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StartCapture(Action<float[]> samples) => _started.TrySetResult(samples);
        public void StopCapture() { }
        public void Dispose() { }

        /// One utterance as the detector would hear it: silence, a voiced stretch, silence.
        public async Task SayAsync(CancellationToken ct) {
            var push = await _started.Task.WaitAsync(ct);
            foreach (var chunk in new[] { UtteranceDetectorTests.Silence(1_000), UtteranceDetectorTests.Voice(700, 0.2f), UtteranceDetectorTests.Silence(1_500) })
                push(chunk);
        }
    }

    sealed class ScriptedRecognizer(Queue<string> heard) : ISpeechRecognizer {
        public int Calls { get; private set; }

        public Task<string> TranscribeAsync(float[] samples, string? vocabulary, CancellationToken ct) {
            Calls++;
            return Task.FromResult(heard.TryDequeue(out var text) ? text : "");
        }

        public void Dispose() { }
    }

    sealed class FakeSpeech : ISpeechOutput {
        public Channel<string> Spoken { get; } = Channel.CreateUnbounded<string>();
        public Channel<VoiceCue> Cues { get; } = Channel.CreateUnbounded<VoiceCue>();
        public IReadOnlyList<string> Voices => ["Test Voice"];
        public void Configure(string? voice, int rate) { }

        /// While set, speech lasts until StopSpeaking cuts it off, as a long reply would.
        public bool Holds { get; set; }
        public int Stops { get; private set; }
        TaskCompletionSource? _talking;

        public Task SpeakAsync(string text, CancellationToken ct) {
            Spoken.Writer.TryWrite(text);
            if (!Holds) return Task.CompletedTask;
            _talking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _talking.Task.WaitAsync(ct);
        }

        public void StopSpeaking() {
            Stops++;
            _talking?.TrySetResult();
        }

        public Task PlayCueAsync(VoiceCue cue) {
            Cues.Writer.TryWrite(cue);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    sealed class ScriptedInterpreter(params IReadOnlyList<VoiceCommand>?[] answers) : IVoiceInterpreter {
        readonly Queue<IReadOnlyList<VoiceCommand>?> _answers = new(answers);
        public List<string> Heard { get; } = [];
        public IObservable<string> Status => System.Reactive.Linq.Observable.Return("Ready.");
        public Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VoiceCommand>?> InterpretAsync(string utterance, VoiceContext context, CancellationToken ct) {
            Heard.Add(utterance);
            return Task.FromResult(_answers.TryDequeue(out var answer) ? answer : null);
        }
    }

    sealed record Rig(VoiceAssistant Assistant, VoiceSettingsService Settings, FakeVoiceHost Host, FakeMicrophone Microphone, FakeSpeech Speech,
            FakeTimeProvider Time) : IDisposable {
        public void Dispose() {
            Assistant.Dispose();
            Settings.Dispose();
        }
    }

    Rig Start(FakeVoiceHost host, params string[] heard) {
        var recognizer = new ScriptedRecognizer(new Queue<string>(heard));
        return Start(host, _ => recognizer);
    }

    Rig Start(FakeVoiceHost host, Func<string, ISpeechRecognizer> openRecognizer, IVoiceInterpreter? interpreter = null) {
        var models = new VoiceModelStore(Tmp.CreateDir("models"));
        File.WriteAllText(models.PathFor(VoiceModelSize.BaseEn), "model");
        File.WriteAllText(models.PathFor(VoiceAssistant.WakeModel), "model");
        var settings = new VoiceSettingsService(Tmp.PathTo("voice.json"));
        settings.SaveAsync(new VoicePreferences(Enabled: false, WakeWord: "computer",
            Understanding: interpreter is null ? VoiceUnderstanding.Phrases : VoiceUnderstanding.Local)).GetAwaiter().GetResult();
        var microphone = new FakeMicrophone();
        var speech = new FakeSpeech();
        var time = new FakeTimeProvider();
        var assistant = new VoiceAssistant(settings, models, new VoiceDevices(_ => microphone, () => speech),
            openRecognizer, host, work => work(), time, understanding => understanding == VoiceUnderstanding.Local ? interpreter : null);
        _ = settings.UpdateAsync(p => p with { Enabled = true });
        return new Rig(assistant, settings, host, microphone, speech, time);
    }

    static CancellationToken Deadline() => new CancellationTokenSource(Limit).Token;

    static async Task<string> NextSpokenAsync(Rig rig, CancellationToken ct) => await rig.Speech.Spoken.Reader.ReadAsync(ct);

    [Test]
    public async Task A_prompt_after_the_wake_word_is_sent_and_acknowledged_aloud() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, run the unit tests.");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Sent to Fix login.");
        await Assert.That(rig.Host.Calls).Contains("send run the unit tests.");
    }

    [Test]
    public async Task The_command_model_rereads_only_what_follows_the_wake_word() {
        var wake = new ScriptedRecognizer(new Queue<string>(["I wonder if it rains", "Computer, run the unit tess"]));
        var command = new ScriptedRecognizer(new Queue<string>(["Computer, run the unit tests."]));
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, path => path.EndsWith(VoiceAssistant.WakeModel.FileName(), StringComparison.Ordinal) ? wake : command);
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Sent to Fix login.");
        await Assert.That(host.Calls).Contains("send run the unit tests.");
        // Chatter without the wake word never reaches the slower model.
        await Assert.That(command.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Speech_without_the_wake_word_does_nothing() {
        using var rig = Start(new FakeVoiceHost(), "run the unit tests", "Computer, status");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        // Only the second utterance is a command: the first left no trace.
        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Daemon connected.");
        await Assert.That(rig.Host.Calls.Any(c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("send", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task The_wake_word_alone_opens_a_window_for_the_command() {
        using var rig = Start(new FakeVoiceHost(), "Computer.", "Open settings.");
        var ct = Deadline();
        await Assert.That(await rig.Speech.Cues.Reader.ReadAsync(ct)).IsEqualTo(VoiceCue.Listening);

        await rig.Microphone.SayAsync(ct);
        await Assert.That(await rig.Speech.Cues.Reader.ReadAsync(ct)).IsEqualTo(VoiceCue.Listening);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Settings.");
        await Assert.That(rig.Host.Calls).Contains("settings");
    }

    [Test]
    public async Task Stopping_the_agent_happens_only_on_a_yes() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, stop the agent", "yes");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Stop Fix login? Say yes to confirm.");
        await Assert.That(rig.Host.Calls).DoesNotContain("stop");

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Microphone.SayAsync(ct);
        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Stopping.");
        await Assert.That(rig.Host.Calls).Contains("stop");
    }

    [Test]
    public async Task A_yes_said_with_the_wake_word_confirms() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, stop the agent", "Computer, yes");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Stopping.");
        await Assert.That(rig.Host.Calls).Contains("stop");
    }

    [Test]
    public async Task A_yes_with_the_wake_word_over_the_question_confirms() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, stop the agent", "Computer, yes");
        rig.Speech.Holds = true;
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        // The question is still being spoken.
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Stopping.");
        await Assert.That(rig.Host.Calls).Contains("stop");
    }

    [Test]
    public async Task A_near_miss_of_the_wake_word_is_settled_by_the_command_model() {
        var wake = new ScriptedRecognizer(new Queue<string>(["Keycap, status", "Keep going"]));
        var command = new ScriptedRecognizer(new Queue<string>(["KCap, status", "Keep going"]));
        using var rig = Start(new FakeVoiceHost(), path => path.EndsWith(VoiceAssistant.WakeModel.FileName(), StringComparison.Ordinal) ? wake : command);
        await rig.Settings.UpdateAsync(p => p with { WakeWord = "KCap" });
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Daemon connected.");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Microphone.SayAsync(ct);

        await WaitForAsync(() => command.Calls == 2, ct);
        await Assert.That(rig.Speech.Spoken.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Anything_but_yes_cancels_a_confirmation() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, stop the agent", "maybe later");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Cancelled.");
        await Assert.That(rig.Host.Calls).DoesNotContain("stop");
    }

    [Test]
    public async Task A_yes_that_comes_after_the_window_closes_is_not_a_confirmation() {
        using var rig = Start(new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" },
            "Computer, stop the agent", "yes", "Computer, status");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        // The window reopens when the question has been spoken; only then does the clock move past it.
        await rig.Assistant.Status.FirstAsync(st => st.State == VoiceState.AwaitingAnswer).ToTask(ct);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Daemon connected.");
        await Assert.That(rig.Host.Calls).DoesNotContain("stop");
    }

    [Test]
    public async Task The_reply_to_a_spoken_prompt_is_read_aloud() {
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, "Computer, run the unit tests.");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        host.Turn.OnNext(new VoiceNarration.Reply("All **42** tests pass."));

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("All 42 tests pass.");
    }

    [Test]
    public async Task A_long_reply_is_outlined_and_then_steered_without_the_wake_word() {
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, "Computer, fix the login bug.", "my cat is on the keyboard", "read the tests part");
        var ct = Deadline();
        var reply = string.Join("\n\n", new[] { "Summary", "Changes", "Tests" }
            .Select(h => $"## {h}\n{h} details. " + string.Join(" ", Enumerable.Repeat("There is a fair amount to say here.", 5))));

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        host.Turn.OnNext(new VoiceNarration.Reply("Fixed it.\n\n" + reply));

        await Assert.That(await NextSpokenAsync(rig, ct)).StartsWith("Fixed it. The reply has 3 parts: Summary, Changes and Tests.");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        // Chatter in the window is not a command: only steering the reply skips the wake word.
        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).StartsWith("Tests. Tests details.");
        await Assert.That(host.Calls.Count(c => c.StartsWith("send", StringComparison.Ordinal))).IsEqualTo(1);
    }

    static string LongReply() => "Fixed it.\n\n" + string.Join("\n\n", new[] { "Summary", "Changes", "Tests" }
        .Select(h => $"## {h}\n{h} details. " + string.Join(" ", Enumerable.Repeat("There is a fair amount to say here.", 5))));

    [Test]
    public async Task Read_more_said_over_the_narration_cuts_it_off_and_reads_on() {
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, "Computer, fix the login bug.", "read more");
        var ct = Deadline();
        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        rig.Speech.Holds = true;

        host.Turn.OnNext(new VoiceNarration.Reply(LongReply()));
        await Assert.That(await NextSpokenAsync(rig, ct)).StartsWith("Fixed it. The reply has 3 parts");
        // Still talking: the listener must hear this rather than wait for the outline to end.
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).StartsWith("Summary. Summary details.");
        await Assert.That(rig.Speech.Stops).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Stop_said_over_the_app_silences_it_without_the_wake_word() {
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, "Computer, fix the login bug.", "stop");
        var ct = Deadline();
        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        rig.Speech.Holds = true;
        host.Turn.OnNext(new VoiceNarration.Reply(LongReply()));
        await NextSpokenAsync(rig, ct);
        var before = rig.Speech.Stops;

        await rig.Microphone.SayAsync(ct);

        await WaitForAsync(() => rig.Speech.Stops > before, ct);
        await Assert.That(host.Calls.Count(c => c.StartsWith("send", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task The_apps_own_longer_sentences_heard_back_do_not_steer_it() {
        var host = new FakeVoiceHost { SessionList = [new("local:a", "Fix login", "", false)], OpenId = "local:a" };
        using var rig = Start(host, "Computer, fix the login bug.", "say read more, name a part, or read it all", "Computer, status");
        var ct = Deadline();
        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        rig.Speech.Holds = true;
        host.Turn.OnNext(new VoiceNarration.Reply(LongReply()));
        await NextSpokenAsync(rig, ct);

        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        // The echo was skipped, so the next thing said is the status, not a part of the reply.
        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Daemon connected.");
    }

    static async Task WaitForAsync(Func<bool> condition, CancellationToken ct) {
        while (!condition()) {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    static readonly List<VoiceSession> LoginAndDocs = [new("local:a", "Fix login", "", false), new("local:b", "Update the docs", "", false)];

    Rig StartInterpreting(FakeVoiceHost host, ScriptedInterpreter interpreter, params string[] heard) {
        var recognizer = new ScriptedRecognizer(new Queue<string>(heard));
        return Start(host, _ => recognizer, interpreter);
    }

    [Test]
    public async Task Words_no_phrase_matches_are_carried_out_as_the_interpreter_reads_them() {
        var host = new FakeVoiceHost { SessionList = LoginAndDocs, OpenId = "local:a" };
        var interpreter = new ScriptedInterpreter([new VoiceCommand.OpenSession("Update the docs"), new VoiceCommand.StopAgent()]);
        using var rig = StartInterpreting(host, interpreter, "Computer, close the docs one");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Opened Update the docs. Stop Update the docs? Say yes to confirm.");
        await Assert.That(interpreter.Heard).IsEquivalentTo(["close the docs one"]);
        await Assert.That(host.Calls).Contains("open local:b");
        await Assert.That(host.Calls).DoesNotContain("stop");
    }

    [Test]
    public async Task Fixed_phrases_and_explicit_prompts_never_wait_for_the_interpreter() {
        var host = new FakeVoiceHost { SessionList = LoginAndDocs, OpenId = "local:a" };
        var interpreter = new ScriptedInterpreter();
        using var rig = StartInterpreting(host, interpreter, "Computer, next session", "Computer, tell it to close the docs");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Sent to Update the docs.");
        await Assert.That(host.Calls).Contains("send close the docs");
        await Assert.That(interpreter.Heard).IsEmpty();
    }

    [Test]
    public async Task Without_an_answer_from_the_interpreter_the_words_go_to_the_agent() {
        var host = new FakeVoiceHost { SessionList = LoginAndDocs, OpenId = "local:a" };
        using var rig = StartInterpreting(host, new ScriptedInterpreter([null]), "Computer, run the unit tests");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Sent to Fix login.");
        await Assert.That(host.Calls).Contains("send run the unit tests");
    }

    [Test]
    public async Task Stop_listening_turns_voice_control_off() {
        using var rig = Start(new FakeVoiceHost(), "Computer, stop listening");
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await NextSpokenAsync(rig, ct);

        var off = await rig.Settings.Changes.FirstAsync(p => !p.Enabled).ToTask(ct);
        await Assert.That(off.Enabled).IsFalse();
    }

    [Test]
    public async Task A_custom_wake_word_replaces_the_default() {
        var host = new FakeVoiceHost();
        using var rig = Start(host, "Computer, status", "Jarvis, status");
        await rig.Settings.UpdateAsync(p => p with { WakeWord = "Jarvis" });
        var ct = Deadline();

        await rig.Microphone.SayAsync(ct);
        await rig.Microphone.SayAsync(ct);

        await Assert.That(await NextSpokenAsync(rig, ct)).IsEqualTo("Daemon connected.");
        await Assert.That(rig.Speech.Spoken.Reader.TryRead(out _)).IsFalse();
    }
}
