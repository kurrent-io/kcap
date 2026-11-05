using System.Reactive.Subjects;

namespace Capacitor.App.Services.Voice;

/// The one Kokoro model for the app's lifetime: downloaded on first use, loaded once, shared by
/// every speech output voice control opens. Loading takes seconds, so it runs in the background
/// and speech uses the Windows voice until it is ready.
public sealed class KokoroEngine : IDisposable {
    readonly KokoroModelStore _store;
    readonly HttpClient? _ownedHttp;
    readonly int _threads;
    readonly Lock _lock = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly BehaviorSubject<string?> _status = new(null);
    Task<KokoroSynthesizer?>? _loading;
    KokoroSynthesizer? _ready;

    public KokoroEngine(string modelsDirectory, HttpClient? http = null) {
        _ownedHttp = http is null ? new HttpClient { Timeout = TimeSpan.FromHours(1) } : null;
        _store = new KokoroModelStore(modelsDirectory, http ?? _ownedHttp!);
        // On a CPU with performance and efficiency cores, threads that land on the slow ones hold the
        // rest back: measured on a 24-core i9, 16 threads took nearly twice as long as 8.
        _threads = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
    }

    /// The loaded model, or null until it is.
    public KokoroSynthesizer? Ready => Volatile.Read(ref _ready);

    /// A line for Settings about the natural voice: downloading, loading, ready, or why it is not.
    public IObservable<string?> Status => _status;

    /// Starts loading if nothing has; a failed load is tried again on the next call.
    public Task<KokoroSynthesizer?> EnsureLoadedAsync() {
        lock (_lock) {
            if (_lifetime.IsCancellationRequested) return Task.FromResult<KokoroSynthesizer?>(null);
            return _loading ??= Task.Run(LoadAsync);
        }
    }

    async Task<KokoroSynthesizer?> LoadAsync() {
        try {
            _status.OnNext(_store.IsReady ? "Loading the natural voice…" : "Downloading the natural voice (350 MB)…");
            var directory = await _store.EnsureAsync(new Progress<double>(fraction =>
                _status.OnNext($"Downloading the natural voice… {fraction:P0}")), _lifetime.Token).ConfigureAwait(false);
            _status.OnNext("Loading the natural voice…");
            var synthesizer = new KokoroSynthesizer(directory, _threads);
            // The first generation is several times slower than the rest; spend it here, not on a reply.
            synthesizer.Generate("Ready.", KokoroVoice.English[0], 1f);
            lock (_lock) {
                if (_lifetime.IsCancellationRequested) {
                    synthesizer.Dispose();
                    return null;
                }
                Volatile.Write(ref _ready, synthesizer);
            }
            _status.OnNext("Natural voice ready.");
            return synthesizer;
        } catch (Exception ex) when (!_lifetime.IsCancellationRequested) {
            _status.OnNext($"The natural voice is unavailable, so the Windows voice speaks instead: {ex.Message}");
            lock (_lock) _loading = null;
            return null;
        } catch (OperationCanceledException) {
            return null;
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            _ready?.Dispose();
            _ready = null;
        }
        _ownedHttp?.Dispose();
        _status.OnCompleted();
        _status.Dispose();
    }
}
