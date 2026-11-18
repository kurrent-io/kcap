using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Services;

/// Diagnostic for what keeps an idle window waking, written once a second to <c>app-idle-trace.jsonl</c>
/// beside the lifecycle log when <c>KCAP_APP_IDLE_TRACE=1</c>. It reaches into Avalonia internals by
/// reflection, so any hook it cannot find is reported in <c>missing</c> rather than failing the app.
/// Its own one-second timer is left out of the timer list.
public static class IdleTrace {
    public const string EnvVar = "KCAP_APP_IDLE_TRACE";
    public const string FileName = "app-idle-trace.jsonl";

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const int MaxStacksPerSecond = 200;
    const int MaxObserversDescribed = 40;
    const int WalkNodeBudget = 400;
    const int WalkDepth = 16;

    static long _loopTicks;
    static long _uiRenders;
    static long _commitRequests;
    static readonly Dictionary<string, int> Stacks = new(StringComparer.Ordinal);
    static readonly Dictionary<string, int> DirtyVisuals = new(StringComparer.Ordinal);
    static readonly HashSet<object> WrappedCompositors = new(ReferenceEqualityComparer.Instance);
    static readonly List<string> Missing = [];

    static FileStream? _out;
    static DispatcherTimer? _timer;
    static object? _mediaContext;
    static object? _renderLoop;
    static bool _renderWrapped, _loopWrapped;
    static long _lastBatchId = -1;
    static long _lastCompleted;
    static int[] _lastGc = [0, 0, 0];
    static TimeSpan _lastCpu, _lastGcPause;
    static long _lastStamp;

    public static void StartIfEnabled(string directory) {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1") return;
        try {
            Directory.CreateDirectory(directory);
            _out = new FileStream(Path.Combine(directory, FileName),
                new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite });
            _lastCompleted = ThreadPool.CompletedWorkItemCount;
            _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
            _lastGcPause = GC.GetTotalPauseDuration();
            _lastGc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            _lastStamp = TimeProvider.System.GetTimestamp();
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Second());
            _timer.Start();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: idle trace disabled: {ex.Message}");
        }
    }

    static void Second() {
        try {
            Missing.Clear();
            EnsureHooks();
            Write();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: idle trace: {ex}");
        }
    }

    // Hooks are retried every second: the render loop and compositor appear only once a window exists.
    static void EnsureHooks() {
        _mediaContext ??= typeof(Visual).Assembly.GetType("Avalonia.Media.MediaContext")?.GetProperty("Instance", Any)?.GetValue(null);
        if (_mediaContext is null) { Missing.Add("MediaContext"); return; }

        if (!_renderWrapped) {
            var field = _mediaContext.GetType().GetField("_render", Any);
            if (field?.GetValue(_mediaContext) is Action original) {
                field.SetValue(_mediaContext, (Action)(() => { BeforeUiRender(); original(); }));
                _renderWrapped = true;
            } else Missing.Add("MediaContext._render");
        }

        foreach (var info in TopLevelInfos()) {
            var compositor = info.GetType().GetProperty("Compositor", Any)?.GetValue(info);
            if (compositor is null || !WrappedCompositors.Add(compositor)) continue;
            var field = compositor.GetType().GetField("_triggerCommitRequested", Any);
            if (field?.GetValue(compositor) is Action original) {
                field.SetValue(compositor, (Action)(() => { OnCommitRequested(); original(); }));
            } else Missing.Add("Compositor._triggerCommitRequested");
            _renderLoop ??= compositor.GetType().GetProperty("Loop", Any)?.GetValue(compositor);
        }

        if (!_loopWrapped && _renderLoop is not null) {
            var field = _renderLoop.GetType().GetField("_tick", Any);
            if (field?.GetValue(_renderLoop) is Action<TimeSpan> original) {
                field.SetValue(_renderLoop, (Action<TimeSpan>)(t => { Interlocked.Increment(ref _loopTicks); original(t); }));
                _loopWrapped = true;
            } else Missing.Add($"{_renderLoop.GetType().Name}._tick");
        }
    }

    static IEnumerable<object> TopLevelInfos() {
        if (_mediaContext?.GetType().GetField("_topLevels", Any)?.GetValue(_mediaContext) is not IDictionary map) return [];
        return [.. map.Values.Cast<object>()];
    }

    static void BeforeUiRender() {
        _uiRenders++;
        foreach (var info in TopLevelInfos()) {
            var renderer = info.GetType().GetProperty("Renderer", Any)?.GetValue(info);
            if (renderer?.GetType().GetField("_dirty", Any)?.GetValue(renderer) is not IEnumerable dirty) continue;
            foreach (var v in dirty) {
                if (v is not Visual visual) continue;
                var key = VisualKey(visual);
                DirtyVisuals[key] = DirtyVisuals.GetValueOrDefault(key) + 1;
            }
        }
    }

    static void OnCommitRequested() {
        if (++_commitRequests > MaxStacksPerSecond) return;
        var frames = new StackTrace(2, false).GetFrames();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var frame in frames) {
            var m = frame.GetMethod();
            if (m is null) continue;
            if (n++ > 0) sb.Append(" < ");
            sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
            if (n >= 16) break;
        }
        var key = sb.ToString();
        Stacks[key] = Stacks.GetValueOrDefault(key) + 1;
    }

    static void Write() {
        if (_out is null) return;
        var now = TimeProvider.System.GetTimestamp();
        var seconds = TimeProvider.System.GetElapsedTime(_lastStamp, now).TotalSeconds;
        _lastStamp = now;

        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var completed = ThreadPool.CompletedWorkItemCount;
        int[] gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var pause = GC.GetTotalPauseDuration();
        var batchId = LastBatchId();

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
            w.WriteStartObject();
            w.WriteString("ts", TimeProvider.System.GetUtcNow().ToString("o"));
            w.WriteNumber("window_s", Math.Round(seconds, 3));
            w.WriteNumber("cpu_pct", Math.Round((cpu - _lastCpu).TotalSeconds / seconds * 100, 2));
            w.WriteBoolean("window_active", AnyWindowActive());
            w.WriteNumber("render_loop_ticks", Interlocked.Exchange(ref _loopTicks, 0));
            w.WriteBoolean("render_loop_running", ReadField(_renderLoop, "_running") is true);
            w.WriteNumber("batches_applied", batchId < 0 || _lastBatchId < 0 ? -1 : batchId - _lastBatchId);
            w.WriteNumber("ui_render_passes", _uiRenders);
            w.WriteNumber("commit_requests", _commitRequests);

            w.WriteStartArray("commit_stacks");
            foreach (var (stack, count) in Stacks.OrderByDescending(kv => kv.Value).Take(8)) {
                w.WriteStartObject();
                w.WriteNumber("n", count);
                w.WriteString("stack", stack);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("dirty_visuals");
            foreach (var (visual, count) in DirtyVisuals.OrderByDescending(kv => kv.Value).Take(12)) {
                w.WriteStartObject();
                w.WriteNumber("n", count);
                w.WriteString("visual", visual);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            WriteClock(w);
            WriteTimers(w);

            w.WriteNumber("threadpool_completed", completed - _lastCompleted);
            w.WriteNumber("threadpool_threads", ThreadPool.ThreadCount);
            w.WriteNumber("threadpool_pending", ThreadPool.PendingWorkItemCount);
            w.WriteNumber("timer_active_count", Timer.ActiveCount);
            w.WriteNumber("lock_contentions", Monitor.LockContentionCount);
            w.WriteStartObject("gc");
            w.WriteNumber("gen0", gc[0] - _lastGc[0]);
            w.WriteNumber("gen1", gc[1] - _lastGc[1]);
            w.WriteNumber("gen2", gc[2] - _lastGc[2]);
            w.WriteNumber("pause_ms", Math.Round((pause - _lastGcPause).TotalMilliseconds, 2));
            w.WriteNumber("heap_mb", Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
            w.WriteEndObject();

            if (Missing.Count > 0) {
                w.WriteStartArray("missing");
                foreach (var m in Missing) w.WriteStringValue(m);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        _out.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        _out.Flush();

        _lastCpu = cpu;
        _lastCompleted = completed;
        _lastGc = gc;
        _lastGcPause = pause;
        if (batchId >= 0) _lastBatchId = batchId;
        _uiRenders = 0;
        _commitRequests = 0;
        Stacks.Clear();
        DirtyVisuals.Clear();
    }

    static long LastBatchId() {
        foreach (var compositor in WrappedCompositors) {
            var server = ReadField(compositor, "_server");
            if (server?.GetType().GetProperty("LastBatchId", Any)?.GetValue(server) is long id) return id;
        }
        return -1;
    }

    // Any subscriber here makes MediaContext run a UI render pass every 16 ms, paused or not.
    static void WriteClock(Utf8JsonWriter w) {
        var clock = ReadField(_mediaContext, "_clock");
        var observers = ReadField(clock, "_observers") as IList;
        var queued = ReadField(clock, "_queuedAnimationFrames") as ICollection;
        var animTimer = ReadField(_mediaContext, "_animationsTimer") as DispatcherTimer;
        w.WriteNumber("clock_observers", observers?.Count ?? -1);
        w.WriteNumber("clock_queued_frames", queued?.Count ?? -1);
        w.WriteBoolean("animations_timer_enabled", animTimer?.IsEnabled == true);
        w.WriteStartArray("clock_subscribers");
        if (observers is not null) {
            var i = 0;
            foreach (var o in observers) {
                if (o is null) continue;
                if (i++ >= MaxObserversDescribed) break;
                w.WriteStringValue(DescribeObserver(o));
            }
        }
        w.WriteEndArray();
    }

    static void WriteTimers(Utf8JsonWriter w) {
        var timers = ReadField(Dispatcher.UIThread, "_timers") as IList;
        w.WriteNumber("dispatcher_timers", timers?.Count ?? -1);
        w.WriteStartArray("dispatcher_timer_list");
        if (timers is not null) {
            foreach (var t in timers) {
                if (t is not DispatcherTimer timer || ReferenceEquals(timer, _timer)) continue;
                w.WriteStartObject();
                w.WriteNumber("ms", Math.Round(timer.Interval.TotalMilliseconds, 1));
                w.WriteBoolean("on", timer.IsEnabled);
                w.WriteString("tick", HandlerNames(ReadField(timer, "Tick") as Delegate));
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    }

    static string HandlerNames(Delegate? d) {
        if (d is null) return "";
        return string.Join(" | ", d.GetInvocationList().Select(Name));
    }

    // A lambda's closure names only itself; the delegates it captured name the code that runs.
    static string Name(Delegate h) => Name(h, captures: true);

    static string Name(Delegate h, bool captures) {
        var name = $"{h.Method.DeclaringType?.FullName}.{h.Method.Name}";
        if (h.Target is null || !captures) return name;
        var captured = h.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => f.GetValue(h.Target)).OfType<Delegate>().Select(d => Name(d, captures: false)).ToList();
        return captured.Count == 0 ? $"{name} @{h.Target.GetType().Name}" : $"{name} -> [{string.Join(", ", captured)}]";
    }

    // Walks the observer's object graph to the animated target, its property and its clock state.
    static string DescribeObserver(object root) {
        string? visual = null, animatable = null, property = null, playState = null, animation = null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < WalkNodeBudget) {
            var (node, depth) = queue.Dequeue();
            if (!seen.Add(node)) continue;
            switch (node) {
                case Visual v:
                    visual ??= VisualKey(v) + $" visible={v.IsEffectivelyVisible} path={Ancestry(v)}";
                    continue;
                case Animatable a:
                    animatable ??= a.GetType().Name;
                    continue;
                case AvaloniaProperty p:
                    property ??= $"{p.OwnerType.Name}.{p.Name}";
                    continue;
                case Animation an:
                    animation ??= $"Animation {an.Duration} x{an.IterationCount}";
                    continue;
                case Type or string or Dispatcher or Delegate { Target: null }:
                    continue;
            }
            var type = node.GetType();
            if (type.Name is "MediaContext" or "MediaContextClock") continue;
            if (playState is null && type.GetProperty("PlayState", Any) is { } ps && ps.PropertyType == typeof(PlayState))
                playState = $"{type.Name}:{ps.GetValue(node)}";
            if (depth >= WalkDepth) continue;
            if (node is Delegate del) {
                foreach (var h in del.GetInvocationList())
                    if (h.Target is { } target) queue.Enqueue((target, depth + 1));
                continue;
            }
            if (node is Array array) {
                if (array.Rank != 1 || array.GetType().GetElementType()!.IsValueType) continue;
                for (var i = 0; i < Math.Min(array.Length, 16); i++)
                    if (array.GetValue(i) is { } item) queue.Enqueue((item, depth + 1));
                continue;
            }
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType) {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    if (f.FieldType.IsValueType || f.FieldType.IsPointer) continue;
                    if (f.GetValue(node) is { } child) queue.Enqueue((child, depth + 1));
                }
            }
        }
        return $"{root.GetType().Name}: target={visual ?? animatable ?? "?"} animatable={animatable ?? "-"} property={property ?? "?"} state={playState ?? "?"} {animation}".TrimEnd();
    }

    static string VisualKey(Visual v) {
        var sb = new StringBuilder(v.GetType().Name);
        if (v is StyledElement { Name: { Length: > 0 } name }) sb.Append('#').Append(name);
        if (v is StyledElement { Classes.Count: > 0 } se) sb.Append('.').Append(string.Join('.', se.Classes));
        return sb.ToString();
    }

    static string Ancestry(Visual v) {
        var parts = new List<string>();
        for (var p = v.GetVisualParent(); p is not null && parts.Count < 40; p = p.GetVisualParent()) {
            if (p is StyledElement { Name: { Length: > 0 } name }) parts.Add($"{p.GetType().Name}#{name}");
            else if (p is UserControl or Window) parts.Add(p.GetType().Name);
        }
        return string.Join('<', parts.Take(8));
    }

    static bool AnyWindowActive() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.Windows.Any(win => win.IsActive);

    static object? ReadField(object? target, string name) {
        if (target is null) return null;
        for (var t = target.GetType(); t is not null; t = t.BaseType) {
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } f)
                return f.GetValue(target);
        }
        return null;
    }
}
