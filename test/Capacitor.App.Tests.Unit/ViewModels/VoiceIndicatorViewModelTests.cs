using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Voice;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class VoiceIndicatorViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task The_circle_starts_listening_and_the_square_stops_it() {
        using var settings = new VoiceSettingsService(Tmp.PathTo("voice.json"));
        var status = new BehaviorSubject<VoiceStatus>(VoiceStatus.Off);
        using var vm = new VoiceIndicatorViewModel(status, settings, scheduler: ImmediateScheduler.Instance);
        await Assert.That(vm.IsOn).IsFalse();
        await Assert.That(vm.ActionName).IsEqualTo("Start listening");

        await vm.ToggleCommand.Execute().ToTask();
        await Assert.That(settings.Current.Enabled).IsTrue();

        status.OnNext(new VoiceStatus(VoiceState.Listening, "Listening for “computer”."));
        await Assert.That(vm.IsOn).IsTrue();
        await Assert.That(vm.ActionName).IsEqualTo("Stop listening");

        await vm.ToggleCommand.Execute().ToTask();
        await Assert.That(settings.Current.Enabled).IsFalse();
    }

    [Test]
    public async Task The_halo_follows_the_voice_and_eases_back_between_words() {
        using var settings = new VoiceSettingsService(Tmp.PathTo("voice.json"));
        var status = new BehaviorSubject<VoiceStatus>(new VoiceStatus(VoiceState.Listening, "Listening."));
        var levels = new Subject<double>();
        using var vm = new VoiceIndicatorViewModel(status, settings, levels, ImmediateScheduler.Instance);
        var resting = vm.HaloSize;

        levels.OnNext(1);
        var speaking = vm.HaloSize;
        levels.OnNext(0);
        var easing = vm.HaloSize;

        await Assert.That(speaking).IsGreaterThan(resting);
        // Not straight back to rest: one quiet buffer between syllables must not make it flicker.
        await Assert.That(easing).IsGreaterThan(resting).And.IsLessThan(speaking);

        status.OnNext(VoiceStatus.Off);
        await Assert.That(vm.HaloSize).IsEqualTo(0);
    }

    [Test]
    public async Task Speech_without_the_wake_word_shows_what_was_heard_and_why_nothing_happened() {
        using var settings = new VoiceSettingsService(Tmp.PathTo("voice.json"));
        var status = new BehaviorSubject<VoiceStatus>(new VoiceStatus(VoiceState.Listening, "Listening.") { WakeWord = "jarvis" });
        using var vm = new VoiceIndicatorViewModel(status, settings, scheduler: ImmediateScheduler.Instance);
        await Assert.That(vm.Text).IsEqualTo("Say “jarvis”…");
        await Assert.That(vm.HeardText).IsNull();

        status.OnNext(new VoiceStatus(VoiceState.Listening, "Listening.", "run the tests") { MissedWakeWord = true, WakeWord = "jarvis" });

        await Assert.That(vm.HeardText).IsEqualTo("Heard “run the tests” · say “jarvis” first");
    }

    [Test]
    public async Task Loudness_puts_a_whisper_well_above_a_quiet_room() {
        var room = UtteranceDetectorTestsSamples(0.0004f);
        var whisper = UtteranceDetectorTestsSamples(0.006f);
        var voice = UtteranceDetectorTestsSamples(0.3f);

        await Assert.That(UtteranceDetector.Loudness(room)).IsEqualTo(0);
        await Assert.That(UtteranceDetector.Loudness(whisper)).IsBetween(0.3, 0.7);
        await Assert.That(UtteranceDetector.Loudness(voice)).IsEqualTo(1);
    }

    static float[] UtteranceDetectorTestsSamples(float amplitude) =>
        Enumerable.Range(0, 960).Select(i => (float)Math.Sin(i * 0.1) * amplitude).ToArray();

    [Test]
    public async Task Starting_after_a_failure_restarts_the_listener() {
        using var settings = new VoiceSettingsService(Tmp.PathTo("voice.json"));
        await settings.UpdateAsync(p => p with { Enabled = true });
        var status = new BehaviorSubject<VoiceStatus>(new VoiceStatus(VoiceState.Failed, "No microphone is connected."));
        using var vm = new VoiceIndicatorViewModel(status, settings, scheduler: ImmediateScheduler.Instance);
        var switches = new List<bool>();
        using var _ = settings.Changes.Skip(1).Subscribe(p => switches.Add(p.Enabled));

        // Still switched on after the failure, so the circle shows and a click must go off and on again.
        await Assert.That(vm.IsOn).IsFalse();
        await vm.ToggleCommand.Execute().ToTask();

        await Assert.That(switches).IsEquivalentTo([false, true]);
    }
}
