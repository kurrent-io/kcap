using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;

namespace Capacitor.App.Services.Voice;

/// Always-on voice control: the microphone feeds an utterance detector, each utterance is
/// transcribed locally, and one that opens with the wake word is carried out as a command and
/// answered aloud. Nothing leaves the machine except the one-time model download.
///
/// While the app is speaking the microphone hears it too, so an utterance heard then counts only
/// if it opens with the wake word: that is what lets "computer, stop talking" cut a long reply.
///
/// Two models: the tiny one hears every utterance and only looks for the wake word, and the
/// one chosen in Settings re-reads just the utterances that turn out to be commands. Spotting
/// stays cheap enough to run on every sound in the room, and a prompt still comes through accurately.
public sealed class VoiceAssistant : IDisposable {
    public const VoiceModelSize WakeModel = VoiceModelSize.TinyEn;

    static readonly TimeSpan FollowUpWindow = TimeSpan.FromSeconds(10);
    static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(15);
    static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(60);
    // Covers a local model's first load and a headless CLI's start; past it the words go to the agent.
    static readonly TimeSpan InterpretLimit = TimeSpan.FromSeconds(30);
    // Steering heard over the app's own voice is accepted only this short: its longer sentences
    // ("Say read more, name a part, or read it all") must not steer it.
    const int MaxBargeInWords = 4;
    // Music for a wait that is noticeable: the local model usually answers before it would start.
    static readonly TimeSpan MusicDelay = TimeSpan.FromMilliseconds(600);
    // How long a yes or no still answers the app's question once its window has closed, while
    // nothing has been said since. Past it the question is forgotten rather than answered late.
    static readonly TimeSpan QuestionLife = TimeSpan.FromMinutes(2);
    // How long a yes or no still answers what an agent said last.
    static readonly TimeSpan AgentLife = TimeSpan.FromMinutes(10);
    // What the conversation keeps of a long reply: its end, where a question to the user sits.
    const int ReplyKept = 600;

    readonly VoiceSettingsService _settings;
    readonly VoiceModelStore _models;
    readonly VoiceDevices _devices;
    readonly Func<string, ISpeechRecognizer> _openRecognizer;
    readonly IVoiceHost _host;
    readonly VoiceCommandExecutor _executor;
    readonly Func<Func<Task>, Task> _onUi;
    readonly Func<VoiceUnderstanding, IVoiceInterpreter?> _interpreters;
    readonly Func<double, CancellationToken, Task<IUtteranceDetector>> _openDetector;
    readonly TimeProvider _time;
    readonly BehaviorSubject<VoiceStatus> _status = new(VoiceStatus.Off);
    readonly Subject<double> _levels = new();
    readonly BehaviorSubject<string?> _timings = new(null);
    readonly SerialDisposable _turnWatch = new();
    readonly VoiceConversation _conversation;
    readonly SemaphoreSlim _speakGate = new(1, 1);
    readonly Lock _lock = new();
    readonly IDisposable _settingsSubscription;
    readonly CancellationTokenSource _lifetime = new();

    Run? _run;
    VoicePreferences _preferences;
    int _speaking;
    long _echoUntil;
    VoiceResponse? _pending;
    // What "repeat" says again: the last answer, question or reading.
    VoiceResponse? _lastResponse;
    long _followUpUntil;
    bool _awaitingCommand;
    long _replyUntil;
    int _replyReadings;
    string? _lastHeard;
    ITimer? _readyCue;

    sealed class Run(CancellationTokenSource stop, VoiceModelSize model, string? microphone) {
        public CancellationTokenSource Stop { get; } = stop;
        public VoiceModelSize Model { get; } = model;
        public string? Microphone { get; } = microphone;
        public Task Completion { get; set; } = Task.CompletedTask;
        public IUtteranceDetector? Detector { get; set; }
        public ISpeechOutput? Speech { get; set; }
    }

    public VoiceAssistant(
            VoiceSettingsService settings, VoiceModelStore models, VoiceDevices devices,
            Func<string, ISpeechRecognizer> openRecognizer, IVoiceHost host, Func<Func<Task>, Task> onUi, TimeProvider time,
            Func<VoiceUnderstanding, IVoiceInterpreter?>? interpreters = null,
            Func<double, CancellationToken, Task<IUtteranceDetector>>? openDetector = null) {
        _settings = settings;
        _models = models;
        _devices = devices;
        _openRecognizer = openRecognizer;
        _host = host;
        _executor = new VoiceCommandExecutor(host);
        _onUi = onUi;
        _interpreters = interpreters ?? (_ => null);
        _openDetector = openDetector ?? ((sensitivity, _) => Task.FromResult<IUtteranceDetector>(new UtteranceDetector(sensitivity)));
        _time = time;
        _conversation = new VoiceConversation(time);
        _preferences = settings.Current;
        _settingsSubscription = settings.Changes.Subscribe(Apply);
    }

    /// The current state, then each change, on whichever thread changed it.
    public IObservable<VoiceStatus> Status => _status.DistinctUntilChanged();

    public VoiceStatus CurrentStatus => _status.Value;

    /// How loud the microphone is, 0 to 1, once per capture buffer while listening; 0 when it stops.
    public IObservable<double> Levels => _levels;

    /// How long the last command took, stage by stage, for tuning models and microphones.
    public IObservable<string?> Timings => _timings;

    /// Where commands are saved as audio when Settings asks for it.
    public string RecordingsDirectory => Path.Combine(Path.GetDirectoryName(_models.Directory) ?? _models.Directory, "voice-recordings");

    /// The installed voices, once speech output has opened.
    public IReadOnlyList<string> Voices { get; private set; } = [];

    void Apply(VoicePreferences preferences) {
        lock (_lock) {
            var understandingChanged = preferences.Understanding != _preferences.Understanding || _run is null;
            _preferences = preferences;
            if (preferences.Enabled && understandingChanged && _interpreters(preferences.Understanding) is { } interpreter)
                _ = interpreter.PrepareAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (_run is { } run) {
                if (run.Detector is { } detector) detector.Sensitivity = preferences.Sensitivity;
                if (run.Speech is { } speech) {
                    speech.Configure(preferences.VoiceName, preferences.Rate);
                    speech.InstantAnswers = preferences.InstantAnswers;
                }
                if (!preferences.Enabled || preferences.Model != run.Model || preferences.Microphone != run.Microphone) StopRun();
                else return;
            }
            if (!preferences.Enabled) {
                Publish(VoiceStatus.Off);
                return;
            }
            StartRun(preferences);
        }
    }

    void StartRun(VoicePreferences preferences) {
        var run = new Run(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), preferences.Model, preferences.Microphone);
        _run = run;
        run.Completion = Task.Run(() => RunAsync(run));
    }

    void StopRun() {
        if (_run is not { } run) return;
        _run = null;
        run.Stop.Cancel();
        run.Speech?.StopSpeaking();
        _turnWatch.Disposable = null;
    }

    async Task RunAsync(Run run) {
        var ct = run.Stop.Token;
        ISpeechRecognizer? wakeRecognizer = null;
        ISpeechRecognizer? commandRecognizer = null;
        IMicrophone? microphone = null;
        try {
            var wakePath = await EnsureModelAsync(WakeModel, "wake word", ct).ConfigureAwait(false);
            var commandPath = run.Model == WakeModel ? wakePath : await EnsureModelAsync(run.Model, "command", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Publish(new VoiceStatus(VoiceState.Preparing, "Loading the speech models…"));
            wakeRecognizer = _openRecognizer(wakePath);
            commandRecognizer = commandPath == wakePath ? wakeRecognizer : _openRecognizer(commandPath);
            run.Speech = _devices.OpenSpeech();
            Voices = run.Speech.Voices;
            VoicePreferences preferences;
            lock (_lock) preferences = _preferences;
            run.Speech.Configure(preferences.VoiceName, preferences.Rate);
            run.Speech.InstantAnswers = preferences.InstantAnswers;

            var utterances = Channel.CreateBounded<(float[] Samples, bool Echo)>(new BoundedChannelOptions(4) {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
            });
            var detector = await _openDetector(preferences.Sensitivity, ct).ConfigureAwait(false);
            run.Detector = detector;
            // Whether any of the utterance being collected was captured while the app was talking. Judged
            // as the audio arrives: by the time the utterance ends, the app has usually stopped.
            var echo = false;
            microphone = _devices.OpenMicrophone(run.Microphone);
            microphone.StartCapture(samples => {
                if (ct.IsCancellationRequested) return;
                _levels.OnNext(UtteranceDetector.Loudness(samples));
                var talking = IsTalking();
                foreach (var utterance in detector.Push(samples)) {
                    utterances.Writer.TryWrite((utterance, echo || talking));
                    echo = false;
                }
                if (detector.InSpeech && talking) echo = true;
            });
            await run.Speech.PlayCueAsync(VoiceCue.Listening).ConfigureAwait(false);
            PublishListening();

            await foreach (var (samples, heardEcho) in utterances.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await HandleAsync(run, wakeRecognizer, commandRecognizer, samples, heardEcho, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            Publish(new VoiceStatus(VoiceState.Failed, FailureText(ex)));
        } finally {
            microphone?.Dispose();
            run.Detector?.Dispose();
            if (!_lifetime.IsCancellationRequested) _levels.OnNext(0);
            if (!ReferenceEquals(commandRecognizer, wakeRecognizer)) commandRecognizer?.Dispose();
            wakeRecognizer?.Dispose();
            run.Speech?.Dispose();
            run.Speech = null;
            lock (_lock) {
                if (ReferenceEquals(_run, run)) _run = null;
                if (!_preferences.Enabled && !_lifetime.IsCancellationRequested) Publish(VoiceStatus.Off);
            }
        }
    }

    Task<string> EnsureModelAsync(VoiceModelSize size, string role, CancellationToken ct) {
        Publish(new VoiceStatus(VoiceState.Preparing, _models.Has(size) ? "Loading the speech models…" : $"Downloading the {role} model…"));
        return _models.EnsureAsync(size, new Progress<double>(fraction =>
            Publish(new VoiceStatus(VoiceState.Preparing, $"Downloading the {role} model… {fraction:P0}"))), ct);
    }

    bool IsTalking() => Volatile.Read(ref _speaking) > 0 || _time.GetTimestamp() < Interlocked.Read(ref _echoUntil);

    async Task HandleAsync(Run run, ISpeechRecognizer wakeRecognizer, ISpeechRecognizer commandRecognizer, float[] utterance,
            bool heardEcho, CancellationToken ct) {
        var heardWhileSpeaking = heardEcho || IsTalking();
        var started = _time.GetTimestamp();
        var reading = TimeSpan.Zero;
        _understanding = null;
        string wakeWord;
        bool saveAudio;
        lock (_lock) {
            wakeWord = _preferences.EffectiveWakeWord;
            saveAudio = _preferences.SaveAudio;
        }

        Publish(new VoiceStatus(VoiceState.Working, "Transcribing…", _lastHeard));
        var samples = UtteranceDetector.Amplify(utterance);
        var text = WakeWordMatcher.Clean(
            await wakeRecognizer.TranscribeAsync(samples, Vocabulary(wakeWord), ct).ConfigureAwait(false));
        var spotting = _time.GetElapsedTime(started);
        if (text.Length == 0) {
            PublishListening();
            return;
        }

        var woke = WakeWordMatcher.TryMatch(text, wakeWord, out var afterWake);
        string? precise = null;
        // The small model misspells short made-up words ("Keycap" for "KCap"), so a near miss is
        // settled by the command model rather than ignored.
        if (!woke && !ReferenceEquals(commandRecognizer, wakeRecognizer) && WakeWordMatcher.IsNearMiss(text, wakeWord)) {
            Publish(new VoiceStatus(VoiceState.Working, "Transcribing…", _lastHeard));
            var nearMiss = _time.GetTimestamp();
            precise = WakeWordMatcher.Clean(
                await commandRecognizer.TranscribeAsync(samples, Vocabulary(wakeWord, await TermsAsync().ConfigureAwait(false)), ct)
                    .ConfigureAwait(false));
            woke = WakeWordMatcher.TryMatch(precise, wakeWord, out afterWake);
            reading += _time.GetElapsedTime(nearMiss);
        }
        var inWindow = _time.GetTimestamp() < _followUpUntil;
        // The wake word makes an answer of something heard while the question is still being spoken.
        var inFollowUp = inWindow && (!heardWhileSpeaking || woke);
        var pending = inFollowUp ? _pending : null;
        var awaitingCommand = inFollowUp && _awaitingCommand;
        if (!inWindow) {
            _pending = null;
            _awaitingCommand = false;
        }

        // Without the wake word, only steering is accepted: the reply's own commands while one is being
        // read, and "stop" while the app talks. Someone else talking in the room must not become a prompt.
        var quick = !woke && pending is null && !awaitingCommand ? VoiceCommandParser.Parse(text) : null;
        var isShort = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxBargeInWords;
        var steersReply = quick is not null && _time.GetTimestamp() < _replyUntil && IsReplyNavigation(quick)
            && (!heardWhileSpeaking || isShort);
        var stopsSpeech = quick is VoiceCommand.StopSpeaking && heardWhileSpeaking && isShort;
        if (stopsSpeech) {
            run.Speech?.StopSpeaking();
            _replyUntil = 0;
            PublishListening();
            return;
        }
        if (!woke && pending is null && !awaitingCommand && !steersReply) {
            if (heardWhileSpeaking) {
                PublishListening();
                return;
            }
            _lastHeard = text;
            Publish(new VoiceStatus(VoiceState.Listening, $"Listening for “{wakeWord}”.", text) { MissedWakeWord = true, WakeWord = wakeWord });
            return;
        }

        var command = woke ? afterWake : precise is { Length: > 0 } ? precise : text;
        if (woke && command.Length > 0 && run.Speech is { } heard) _ = heard.PlayCueAsync(VoiceCue.Accepted);
        // Only words meant for an agent or naming something need the better model; "read more" does not.
        if (precise is null && command.Length > 0 && !ReferenceEquals(commandRecognizer, wakeRecognizer)
            && CarriesText(VoiceCommandParser.Parse(command))) {
            Publish(new VoiceStatus(VoiceState.Working, "Transcribing…", _lastHeard));
            using var transcribing = WorkingMusic(run);
            var rereadStarted = _time.GetTimestamp();
            var hint = await TermsAsync().ConfigureAwait(false);
            var reread = WakeWordMatcher.Clean(
                await commandRecognizer.TranscribeAsync(samples, Vocabulary(wakeWord, hint), ct).ConfigureAwait(false));
            reading += _time.GetElapsedTime(rereadStarted);
            // The better model's words win; the wake word it heard too is not part of the command.
            if (WakeWordMatcher.TryMatch(reread, wakeWord, out var preciseCommand)) {
                if (preciseCommand.Length > 0) command = preciseCommand;
            } else if (!woke && reread.Length > 0) {
                command = reread;
            }
        }
        _lastHeard = command.Length > 0 ? command : text;
        _pending = null;
        _awaitingCommand = false;

        if (command.Length == 0) {
            run.Speech?.StopSpeaking();
            _awaitingCommand = true;
            _followUpUntil = Deadline(FollowUpWindow);
            Publish(new VoiceStatus(VoiceState.AwaitingCommand, "Listening for a command…", _lastHeard));
            if (run.Speech is { } cueSpeech) await cueSpeech.PlayCueAsync(VoiceCue.Listening).ConfigureAwait(false);
            return;
        }

        var parsed = VoiceCommandParser.Parse(command);
        // A new command replaces whatever is being said.
        run.Speech?.StopSpeaking();
        if (parsed is VoiceCommand.StopSpeaking) {
            run.Speech?.StopSpeaking();
            _replyUntil = 0;
            PublishListening();
            return;
        }

        Publish(new VoiceStatus(VoiceState.Working, "Working…", _lastHeard));
        VoiceResponse response;
        try {
            response = pending switch {
                _ when parsed is VoiceCommand.Repeat => Volatile.Read(ref _lastResponse) ?? VoiceResponse.Fail("I haven't said anything yet."),
                { } waiting when parsed is VoiceCommand.Wait => waiting with { Speech = "Take your time.", Window = WaitWindow },
                _ when parsed is VoiceCommand.Wait && awaitingCommand => new VoiceResponse("Take your time."),
                _ when parsed is VoiceCommand.Wait => new VoiceResponse("Okay."),
                { FollowUp: VoiceFollowUp.Confirm, OnConfirm: { } confirm } when parsed is VoiceCommand.Confirm =>
                    await OnUiAsync(confirm).ConfigureAwait(false),
                { FollowUp: VoiceFollowUp.Confirm or VoiceFollowUp.Prompt } when parsed is VoiceCommand.Cancel => new VoiceResponse("Cancelled."),
                { FollowUp: VoiceFollowUp.Confirm } when !woke => new VoiceResponse("Cancelled."),
                { FollowUp: VoiceFollowUp.Prompt } when !woke => await OnUiAsync(() => _executor.StartFromFollowUpAsync(command)).ConfigureAwait(false),
                null when parsed is VoiceCommand.Confirm or VoiceCommand.Cancel =>
                    await AnswerLastAsync(run, parsed, command, ct).ConfigureAwait(false),
                _ when parsed is VoiceCommand.Prompt { Explicit: false } && Interpreter() is { } interpreter =>
                    await InterpretAsync(run, interpreter, command, parsed, ct).ConfigureAwait(false),
                _ => await OnUiAsync(() => _executor.ExecuteAsync(parsed)).ConfigureAwait(false),
            };
        } catch (Exception ex) {
            response = VoiceResponse.Fail($"That didn't work: {ex.Message}");
        }
        _conversation.Add(command, response);
        PublishTimings(utterance.Length, spotting, reading, _time.GetElapsedTime(started));
        if (saveAudio) Save(utterance, text, command);
        if (parsed is VoiceCommand.Wait && awaitingCommand && pending is null) {
            _awaitingCommand = true;
            _followUpUntil = Deadline(WaitWindow);
        }
        await RespondAsync(run, response, ct).ConfigureAwait(false);
    }

    // The microphone hears the music too, so it counts as the app talking: only a command that
    // opens with the wake word gets through while it plays.
    IDisposable WorkingMusic(Run run) {
        if (run.Speech is not { } speech) return System.Reactive.Disposables.Disposable.Empty;
        var playing = new System.Reactive.Disposables.SerialDisposable();
        var started = 0;
        var timer = _time.CreateTimer(_ => {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
            Interlocked.Increment(ref _speaking);
            playing.Disposable = speech.PlayWorking();
        }, null, MusicDelay, Timeout.InfiniteTimeSpan);
        return System.Reactive.Disposables.Disposable.Create(() => {
            timer.Dispose();
            if (Interlocked.Exchange(ref started, 2) == 1) {
                playing.Dispose();
                Interlocked.Exchange(ref _echoUntil, Deadline(EchoTail));
                Interlocked.Decrement(ref _speaking);
            }
        });
    }

    TimeSpan? _understanding;

    void PublishTimings(int samples, TimeSpan spotting, TimeSpan reading, TimeSpan total) {
        static string S(TimeSpan t) => t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        var parts = new List<string> {
            $"you spoke {S(TimeSpan.FromSeconds((double)samples / IMicrophone.SampleRate))}",
            $"wake word {S(spotting)}",
        };
        if (reading > TimeSpan.Zero) parts.Add($"transcribing {S(reading)}");
        if (_understanding is { } understanding) parts.Add($"understanding {S(understanding)}");
        parts.Add($"answer after {S(total)}");
        _timings.OnNext(string.Join(" · ", parts));
    }

    // The command as heard, beside what each model made of it, so a misheard word can be traced to
    // the microphone, the wake model or the command model. Only commands: chatter is never kept.
    void Save(float[] utterance, string heard, string command) {
        var directory = Path.Combine(RecordingsDirectory, _time.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var name = _time.GetLocalNow().ToString("HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        _ = Task.Run(() => {
            try {
                Directory.CreateDirectory(directory);
                WaveFile.Write(Path.Combine(directory, name + ".wav"), utterance, IMicrophone.SampleRate);
                File.WriteAllText(Path.Combine(directory, name + ".txt"), $"wake model: {heard}\ncommand: {command}\n");
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        });
    }

    IVoiceInterpreter? Interpreter() {
        VoiceUnderstanding understanding;
        lock (_lock) understanding = _preferences.Understanding;
        return understanding == VoiceUnderstanding.Phrases ? null : _interpreters(understanding);
    }

    // Words no phrase matched: the interpreter says what they mean, and when it cannot answer they
    // go to the agent as they would without one.
    async Task<VoiceResponse> InterpretAsync(Run run, IVoiceInterpreter interpreter, string command, VoiceCommand parsed, CancellationToken ct) {
        Publish(new VoiceStatus(VoiceState.Working, "Understanding…", _lastHeard));
        using var music = WorkingMusic(run);
        VoiceContext? context = null;
        await OnUiAsync(() => {
            context = _executor.Context() with { Recent = _conversation.Recent() };
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        IReadOnlyList<VoiceCommand>? commands;
        var interpreting = _time.GetTimestamp();
        using (var limit = new CancellationTokenSource(InterpretLimit, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token)) {
            try {
                commands = await interpreter.InterpretAsync(command, context!, linked.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                commands = null;
            }
        }
        _understanding = _time.GetElapsedTime(interpreting);
        if (commands is [VoiceCommand.StopSpeaking]) return new VoiceResponse("");
        if (commands is [VoiceCommand.Repeat]) return Volatile.Read(ref _lastResponse) ?? VoiceResponse.Fail("I haven't said anything yet.");
        if (commands is [VoiceCommand.Wait]) return new VoiceResponse("Okay.");
        return commands is { Count: > 0 }
            ? await OnUiAsync(() => _executor.ExecuteAllAsync(commands)).ConfigureAwait(false)
            : await OnUiAsync(() => _executor.ExecuteAsync(parsed)).ConfigureAwait(false);
    }

    // A yes or no with no question open on it answers whatever was said last: the app's question
    // after its window closed, or an agent's reply or request. Failing those, the interpreter reads
    // it against the conversation.
    async Task<VoiceResponse> AnswerLastAsync(Run run, VoiceCommand parsed, string command, CancellationToken ct) {
        var yes = parsed is VoiceCommand.Confirm;
        switch (_conversation.Last(AgentLife)) {
            case { Question.OnConfirm: { } confirm } asked when _time.GetUtcNow() - asked.At < QuestionLife:
                return yes ? await OnUiAsync(confirm).ConfigureAwait(false) : new VoiceResponse("Cancelled.");
            case { Speaker: { } speaker }:
                return await OnUiAsync(() => _executor.AnswerAgentAsync(speaker, command, yes)).ConfigureAwait(false);
        }
        return Interpreter() is { } interpreter && _conversation.Recent().Count > 0
            ? await InterpretAsync(run, interpreter, command, parsed, ct).ConfigureAwait(false)
            : await OnUiAsync(() => _executor.ExecuteAsync(parsed)).ConfigureAwait(false);
    }

    // The answer is spoken in the background: the loop goes back to the microphone at once, so a
    // command can cut off a long answer instead of waiting for it to end.
    async Task RespondAsync(Run run, VoiceResponse response, CancellationToken ct) {
        if (response.FollowUp == VoiceFollowUp.StopListening) {
            _ = SpeakThenAsync(run, response.Speech, false, () => _ = _settings.UpdateAsync(p => p with { Enabled = false }), ct);
            return;
        }
        if (response.Failed && run.Speech is { } speech) _ = speech.PlayCueAsync(VoiceCue.Failed);

        bool readReplies;
        lock (_lock) readReplies = _preferences.ReadReplies;
        if (response.WatchTurn && readReplies) await OnUiAsync(() => {
            _turnWatch.Disposable = _host.WatchNextTurn().Subscribe(narration => _ = NarrateAsync(run, narration));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // Windows open before the answer is spoken, and run from its end once it is.
        _replyUntil = response.OpensReply ? long.MaxValue : 0;
        var reading = Interlocked.Increment(ref _replyReadings);
        var asks = response.FollowUp is VoiceFollowUp.Confirm or VoiceFollowUp.Prompt;
        if (asks) {
            _pending = response;
            _followUpUntil = Deadline(response.Window ?? FollowUpWindow);
        }
        if (response.Speech.Length > 0) Volatile.Write(ref _lastResponse, response);
        PrepareNextPart(run, response.OpensReply);
        _ = SpeakThenAsync(run, response.Speech, response.Reads, () => {
            // Only the latest reading sets the window: a part cut off by "read more" must not close it.
            if (response.OpensReply && reading == Volatile.Read(ref _replyReadings)) _replyUntil = Deadline(ReplyWindow);
            if (asks && ReferenceEquals(_pending, response)) {
                _followUpUntil = Deadline(response.Window ?? FollowUpWindow);
                Publish(new VoiceStatus(VoiceState.AwaitingAnswer,
                    response.FollowUp == VoiceFollowUp.Confirm ? "Waiting for yes or no…" : "Listening for the prompt…", _lastHeard));
            } else {
                PublishListening();
            }
            CueReady(run, reading);
        }, ct);
    }

    // A tone once the answer has been spoken and its echo has died away, so the next words are
    // not taken for the app's own voice. Skipped when a newer command or reading has started since.
    void CueReady(Run run, int reading) {
        if (run.Speech is not { } speech) return;
        // Held in a field: a timer nothing references can be collected before it fires.
        var timer = _time.CreateTimer(_ => {
            if (run.Stop.IsCancellationRequested || reading != Volatile.Read(ref _replyReadings) || IsTalking()) return;
            if (_status.Value.State is not (VoiceState.Listening or VoiceState.AwaitingAnswer or VoiceState.AwaitingCommand)) return;
            _ = speech.PlayCueAsync(VoiceCue.Ready);
        }, null, EchoTail + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _readyCue, timer)?.Dispose();
    }

    async Task SpeakThenAsync(Run run, string text, bool reads, Action then, CancellationToken ct) {
        try {
            await SpeakAsync(run, text, ct, reads).ConfigureAwait(false);
            then();
        } catch (OperationCanceledException) {
        }
    }

    async Task NarrateAsync(Run run, VoiceNarration narration) {
        VoiceSession? speaker = null;
        await OnUiAsync(() => {
            speaker = _host.OpenSession;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        var reply = narration is VoiceNarration.Reply { Text: var markdown } ? _executor.OpenReply(markdown) : null;
        var text = reply?.Speech ?? (narration as VoiceNarration.Attention)?.Text ?? "";
        var said = narration is VoiceNarration.Reply { Text: var full } ? Tail(SpeakableText.From(full, int.MaxValue), ReplyKept) : text;
        _conversation.AddAgent(speaker, said);
        // A reply that ends on a question is answered like a request: without the wake word.
        var asks = narration is VoiceNarration.Attention || said.TrimEnd().EndsWith('?');
        if (text.Length > 0) Volatile.Write(ref _lastResponse, reply ?? new VoiceResponse(text));
        _replyUntil = reply is { OpensReply: true } ? long.MaxValue : 0;
        var reading = Interlocked.Increment(ref _replyReadings);
        PrepareNextPart(run, reply is { OpensReply: true });
        try {
            await SpeakAsync(run, text, run.Stop.Token, reads: reply is not null).ConfigureAwait(false);
            if (reply is { OpensReply: true } && reading == Volatile.Read(ref _replyReadings)) _replyUntil = Deadline(ReplyWindow);
            if (asks) {
                _awaitingCommand = true;
                _followUpUntil = Deadline(FollowUpWindow);
                Publish(new VoiceStatus(VoiceState.AwaitingCommand, "Listening for your answer…", _lastHeard));
            } else {
                PublishListening();
            }
            CueReady(run, reading);
        } catch (OperationCanceledException) {
        }
    }

    // "Read more" is the likely next command; preparing it now lets the part start without a wait.
    void PrepareNextPart(Run run, bool opensReply) {
        if (opensReply && _executor.PeekNextPart() is { } next) run.Speech?.Prepare(Pronunciation.Apply(next));
    }

    async Task SpeakAsync(Run run, string text, CancellationToken ct, bool reads = false) {
        if (string.IsNullOrWhiteSpace(text) || run.Speech is not { } speech) return;
        var spoken = Pronunciation.Apply(text);
        await _speakGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _speaking);
        try {
            Publish(new VoiceStatus(VoiceState.Speaking, text, _lastHeard));
            await (reads ? speech.ReadAsync(spoken, ct) : speech.SpeakAsync(spoken, ct)).ConfigureAwait(false);
        } finally {
            Interlocked.Exchange(ref _echoUntil, Deadline(EchoTail));
            Interlocked.Decrement(ref _speaking);
            _speakGate.Release();
        }
    }

    static bool CarriesText(VoiceCommand command) => command is VoiceCommand.Prompt or VoiceCommand.Dictate
        or VoiceCommand.NewSession { Goal: not null } or VoiceCommand.OpenSession or VoiceCommand.SelectRepository
        or VoiceCommand.SelectHarness or VoiceCommand.ReadPart;

    static bool IsReplyNavigation(VoiceCommand command) =>
        command is VoiceCommand.ReadMore or VoiceCommand.ReadPart or VoiceCommand.ReadAll or VoiceCommand.ReadReply
            or VoiceCommand.PreviousPart or VoiceCommand.Repeat;

    async Task<VoiceResponse> OnUiAsync(Func<Task<VoiceResponse>> work) {
        VoiceResponse? result = null;
        await _onUi(async () => result = await work()).ConfigureAwait(false);
        return result!;
    }

    Task OnUiAsync(Func<Task> work) => _onUi(work);

    // A sentence, not the bare word: primed with the word alone, Whisper takes it as already said and
    // drops it from the start of the transcript.
    internal static string Vocabulary(string wakeWord) =>
        $"Commands to a coding assistant start with {wakeWord}, as in: {wakeWord}, list sessions.";

    // Names Whisper would otherwise spell as words it knows ("clod" for Claude, "codecs" for
    // Codex): with them in the hint, small.en heard "Claude" 20 times in 20 rather than 8. The open
    // session's latest reply goes in too, since a command is most often about what it just said.
    // Whisper keeps the end of a long hint and drops the start, so the least needed part goes first
    // and the wake-word sentence last.
    internal static string Vocabulary(string wakeWord, VoiceHint hint) {
        var sb = new System.Text.StringBuilder();
        if (hint.Terms.Count > 0) sb.Append("It works with ").Append(string.Join(", ", hint.Terms)).Append(". ");
        if (hint.Reply.Length > 0) sb.Append("The agent said: ").Append(hint.Reply).Append(' ');
        return sb.Append(Vocabulary(wakeWord)).ToString();
    }

    // kcap's own words and the agents it runs. Whisper reads only the last couple of hundred tokens
    // of a hint, so the list stays short and the session titles are capped.
    static readonly string[] KcapTerms = [
        "kcap", "Claude", "Codex", "Cursor", "Copilot", "Gemini", "Kiro", "OpenCode", "Grok", "Antigravity",
        "sessions", "work items", "loose ends", "pages", "plans", "flows", "review flow", "evals", "memories",
        "skills", "artifacts", "pull request", "worktree", "daemon", "launcher", "harness",
    ];
    const int HintSessions = 6;
    // About 70 words of the reply: with the terms and titles, the hint stays near Whisper's limit.
    const int HintReplyChars = 400;

    async Task<VoiceHint> TermsAsync() {
        var terms = new List<string>(KcapTerms);
        var reply = "";
        try {
            await OnUiAsync(() => {
                terms.AddRange(_host.Sessions.Take(HintSessions).Select(s => s.Title).Where(t => t.Length > 0));
                if (_host.LastReply is { Length: > 0 } last) reply = Tail(SpeakableText.From(last, int.MaxValue), HintReplyChars);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        } catch (Exception) {
        }
        return new VoiceHint(terms, reply);
    }

    // The end of the reply, from a word start: its last words are the ones a command answers.
    static string Tail(string text, int chars) {
        if (text.Length <= chars) return text;
        var start = text.IndexOf(' ', text.Length - chars);
        return start < 0 ? text[^chars..] : text[(start + 1)..];
    }

    long Deadline(TimeSpan after) => _time.GetTimestamp() + (long)(after.TotalSeconds * _time.TimestampFrequency);

    void PublishListening() {
        string wakeWord;
        lock (_lock) wakeWord = _preferences.EffectiveWakeWord;
        Publish(new VoiceStatus(VoiceState.Listening, $"Listening for “{wakeWord}”.", _lastHeard) { WakeWord = wakeWord });
    }

    void Publish(VoiceStatus status) {
        if (_lifetime.IsCancellationRequested) return;
        _status.OnNext(status);
    }

    static string FailureText(Exception ex) => ex switch {
        HttpRequestException => "Could not download the speech model. Check the connection and turn voice control on again.",
        InvalidOperationException { Message: var message } => message,
        _ => $"Voice control stopped: {ex.Message}",
    };

    public void Dispose() {
        Task completion;
        lock (_lock) {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            completion = _run?.Completion ?? Task.CompletedTask;
            StopRun();
        }
        _settingsSubscription.Dispose();
        try { completion.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _turnWatch.Dispose();
        Interlocked.Exchange(ref _readyCue, null)?.Dispose();
        _status.OnCompleted();
        _status.Dispose();
        _timings.Dispose();
        _levels.OnCompleted();
        _levels.Dispose();
        _speakGate.Dispose();
        _lifetime.Dispose();
    }
}
