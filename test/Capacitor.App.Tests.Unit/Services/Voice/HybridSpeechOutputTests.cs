using System.Net;
using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class HybridSpeechOutputTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Offline : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    sealed class RecordingSpeech : ISpeechOutput {
        public List<string> Spoken { get; } = [];
        public List<string?> Configured { get; } = [];
        public IReadOnlyList<string> Voices => ["Microsoft Zira Desktop"];
        public void Configure(string? voice, int rate) => Configured.Add(voice);

        public Task SpeakAsync(string text, CancellationToken ct) {
            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public void StopSpeaking() { }
        public Task PlayCueAsync(VoiceCue cue) => Task.CompletedTask;
        public void Dispose() { }
    }

    sealed class SilentPlayer : IPcmPlayer {
        public Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct) => Task.CompletedTask;
        public void StopPlayback() { }
        public void Dispose() { }
    }

    [Test]
    public async Task Until_the_natural_voice_loads_the_system_voice_speaks() {
        using var http = new HttpClient(new Offline());
        using var engine = new KokoroEngine(Tmp.PathTo("models"), http);
        var system = new RecordingSpeech();
        using var output = new HybridSpeechOutput(engine, system, new SilentPlayer());

        await output.SpeakAsync("Sent to Fix login.", CancellationToken.None);

        await Assert.That(system.Spoken).IsEquivalentTo(["Sent to Fix login."]);
    }

    [Test]
    [Arguments("windows:Microsoft Zira Desktop", "Microsoft Zira Desktop")]
    [Arguments("Microsoft Zira Desktop", "Microsoft Zira Desktop")]
    [Arguments("kokoro:am_michael", null)]
    public async Task A_windows_voice_reaches_the_system_voice_by_its_own_name(string stored, string? expected) {
        using var http = new HttpClient(new Offline());
        using var engine = new KokoroEngine(Tmp.PathTo("models"), http);
        var system = new RecordingSpeech();
        using var output = new HybridSpeechOutput(engine, system, new SilentPlayer());

        output.Configure(stored, 0);

        await Assert.That(system.Configured[^1]).IsEqualTo(expected);
    }

    [Test]
    public async Task The_voice_list_offers_kokoro_first_then_the_system_voices() {
        using var http = new HttpClient(new Offline());
        using var engine = new KokoroEngine(Tmp.PathTo("models"), http);
        using var output = new HybridSpeechOutput(engine, new RecordingSpeech(), new SilentPlayer());

        await Assert.That(output.Voices[0]).IsEqualTo("kokoro:af_alloy");
        await Assert.That(output.Voices[^1]).IsEqualTo("windows:Microsoft Zira Desktop");
    }

    [Test]
    public async Task Short_sentences_are_joined_so_each_generation_is_worth_its_cost() {
        var pieces = HybridSpeechOutput.Sentences("Done. Tests pass. The reply has three parts: Summary, Changes and Tests. Say more.");

        await Assert.That(pieces).IsEquivalentTo([
            "Done. Tests pass. The reply has three parts:",
            "Summary, Changes and Tests. Say more.",
        ]);
    }

    [Test]
    public async Task A_long_first_sentence_is_cut_at_its_comma_so_speech_starts_sooner() {
        var pieces = HybridSpeechOutput.Pieces("I fixed the redirect loop in the login guard, and added three tests for it. All pass.");

        await Assert.That(pieces[0]).IsEqualTo("I fixed the redirect loop in the login guard,");
        await Assert.That(pieces[1]).StartsWith("and added three tests for it.");
    }

    [Test]
    public async Task A_short_first_sentence_is_left_whole() =>
        await Assert.That(HybridSpeechOutput.Pieces("Done, all good. The rest follows here in full.")[0]).IsEqualTo("Done, all good.");

    [Test]
    public async Task Short_answers_stay_on_the_instant_voice_by_default() {
        using var http = new HttpClient(new Offline());
        using var engine = new KokoroEngine(Tmp.PathTo("models"), http);
        var system = new RecordingSpeech();
        using var output = new HybridSpeechOutput(engine, system, new SilentPlayer());
        output.Configure("kokoro:af_heart", 0);

        await output.SpeakAsync("Allowed.", CancellationToken.None);

        await Assert.That(output.InstantAnswers).IsTrue();
        await Assert.That(system.Spoken).IsEquivalentTo(["Allowed."]);
    }

    [Test]
    [Arguments("kokoro:bm_george", "bm_george")]
    [Arguments("kokoro:retired_voice", "af_heart")]
    public async Task A_stored_key_resolves_to_a_kokoro_voice(string stored, string expected) =>
        await Assert.That(KokoroVoice.FromKey(stored)!.Key).IsEqualTo(expected);

    [Test]
    [Arguments("windows:Microsoft Zira Desktop")]
    [Arguments(null)]
    public async Task A_windows_key_or_no_choice_at_all_is_the_fast_system_voice(string? stored) =>
        await Assert.That(KokoroVoice.FromKey(stored)).IsNull();

    [Test]
    public async Task The_natural_voice_is_not_loaded_until_one_is_chosen() {
        var http = new CountingHttp();
        using var client = new HttpClient(http);
        using var engine = new KokoroEngine(Tmp.PathTo("models"), client);
        using var output = new HybridSpeechOutput(engine, new RecordingSpeech(), new SilentPlayer());
        output.Configure(null, 0);
        await output.SpeakAsync("Sent.", CancellationToken.None);
        await Assert.That(http.Requests).IsEqualTo(0);

        output.Configure("kokoro:af_heart", 0);
        await engine.EnsureLoadedAsync();
        await Assert.That(http.Requests).IsGreaterThan(0);
    }

    sealed class CountingHttp : HttpMessageHandler {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
