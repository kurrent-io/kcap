using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class UtteranceDetectorTests {
    const int Rate = IMicrophone.SampleRate;

    internal static float[] Silence(int ms, float noise = 0.0005f) {
        var samples = new float[Rate * ms / 1000];
        var random = new Random(7);
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(random.NextDouble() * 2 - 1) * noise;
        return samples;
    }

    internal static float[] Voice(int ms, float amplitude) {
        var samples = new float[Rate * ms / 1000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(2 * Math.PI * 220 * i / Rate) * amplitude;
        return samples;
    }

    static List<float[]> Feed(UtteranceDetector detector, params float[][] chunks) {
        var found = new List<float[]>();
        foreach (var chunk in chunks) {
            // The capture device delivers small buffers; feed them that way.
            for (var offset = 0; offset < chunk.Length; offset += 960)
                found.AddRange(detector.Push(chunk.AsSpan(offset, Math.Min(960, chunk.Length - offset))));
        }
        return found;
    }

    [Test]
    public async Task Room_noise_alone_is_never_an_utterance() {
        var detector = new UtteranceDetector(0.6);
        await Assert.That(Feed(detector, Silence(5_000))).IsEmpty();
    }

    [Test]
    public async Task Speech_between_silences_is_one_utterance_with_its_onset_kept() {
        var detector = new UtteranceDetector(0.6);
        var found = Feed(detector, Silence(1_000), Voice(800, 0.2f), Silence(1_500));

        await Assert.That(found).Count().IsEqualTo(1);
        // The pre-roll keeps the quiet start of the first word, so the utterance outlasts the voiced part.
        await Assert.That(found[0].Length).IsGreaterThan(Rate * 800 / 1000);
    }

    [Test]
    public async Task A_pause_shorter_than_the_hangover_does_not_split_a_prompt() {
        var detector = new UtteranceDetector(0.6);
        var found = Feed(detector, Silence(1_000), Voice(600, 0.2f), Silence(500), Voice(600, 0.2f), Silence(1_500));
        await Assert.That(found).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_click_too_short_to_be_a_word_is_dropped() {
        var detector = new UtteranceDetector(0.6);
        await Assert.That(Feed(detector, Silence(1_000), Voice(120, 0.3f), Silence(1_500))).IsEmpty();
    }

    [Test]
    public async Task A_whisper_needs_a_high_sensitivity() {
        var whisper = new[] { Silence(1_000), Voice(800, 0.006f), Silence(1_500) };
        await Assert.That(Feed(new UtteranceDetector(0.1), whisper)).IsEmpty();
        await Assert.That(Feed(new UtteranceDetector(0.9), whisper)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Reset_drops_what_was_being_collected() {
        var detector = new UtteranceDetector(0.6);
        Feed(detector, Silence(1_000), Voice(600, 0.2f));
        await Assert.That(detector.InSpeech).IsTrue();

        detector.Reset();

        await Assert.That(detector.InSpeech).IsFalse();
        await Assert.That(Feed(detector, Silence(1_500))).IsEmpty();
    }

    [Test]
    public async Task Amplify_brings_a_whisper_up_without_exceeding_full_scale() {
        var loud = UtteranceDetector.Amplify(Voice(100, 0.05f));
        await Assert.That(loud.Max(Math.Abs)).IsBetween(0.85f, 0.91f);

        var nearSilent = UtteranceDetector.Amplify(Voice(100, 0.0001f), maxGain: 24f);
        await Assert.That(nearSilent.Max(Math.Abs)).IsLessThan(0.01f);
    }
}
