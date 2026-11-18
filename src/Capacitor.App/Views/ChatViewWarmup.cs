using System.Reactive.Linq;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Builds one chat a second after the main window is first shown, so the first session opened does
/// not pay for the chat view's first construction: its XAML, row templates, markdown and the
/// transcript projection. The view sits in the window transparent and out of hit testing for one
/// frame, over a made-up transcript, and reaches no network, rail or saved state.
/// `KCAP_APP_NO_PREWARM=1` turns it off, for timing the cold path.
internal sealed class ChatViewWarmup(IPermissionService permissions, IUrlOpener opener, TimeProvider time) {
    const string AgentId = "kcap-chat-warmup";
    static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(10);
    static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(50);
    /// A hidden window renders no frames.
    static readonly TimeSpan FrameLimit = TimeSpan.FromMilliseconds(500);

    bool _scheduled;

    internal Task? RunForTesting { get; private set; }

    static bool Disabled => Environment.GetEnvironmentVariable("KCAP_APP_NO_PREWARM") == "1";

    /// Runs at most once per instance; later calls, and any while disabled, do nothing.
    public void Schedule(MainWindow window) {
        if (_scheduled || Disabled) return;
        _scheduled = true;
        RunForTesting = RunAsync(window);
    }

    async Task RunAsync(MainWindow window) {
        try {
            await Task.Delay(IdleDelay, time).ConfigureAwait(false);
            var chat = await Dispatcher.UIThread.InvokeAsync(
                () => Wanted(window) ? Build() : null, DispatcherPriority.Background);
            if (chat is null) return;
            try {
                var deadline = time.GetUtcNow() + DrainLimit;
                while (await Dispatcher.UIThread.InvokeAsync(() => chat.Items.Count == 0, DispatcherPriority.Background)) {
                    if (time.GetUtcNow() >= deadline) return;
                    await Task.Delay(DrainPoll, time).ConfigureAwait(false);
                }
                await Dispatcher.UIThread.InvokeAsync(() => LayOutOnceAsync(window, chat), DispatcherPriority.Background);
            } finally {
                await Dispatcher.UIThread.InvokeAsync(chat.TeardownAsync);
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: chat warm-up: {ex.Message}");
        }
    }

    /// A chat already open has paid the cost this would save.
    static bool Wanted(MainWindow window) =>
        window.IsVisible && !window.GetVisualDescendants().OfType<ChatTabView>().Any();

    ChatTabViewModel Build() =>
        new(AgentId, AgentOrigin.Remote, Observable.Return(WarmupTranscriptFeed.Session(AgentId)), Observable.Return<string[]?>(null),
            new ReadOnlyChatInput(), new NoAttachmentUploader(), _ => new WarmupTranscriptFeed(), opener, time, permissions,
            new SessionRuns(time));

    async Task LayOutOnceAsync(MainWindow window, ChatTabViewModel chat) {
        if (!Wanted(window)) return;
        var view = new ChatTabView { DataContext = chat, Opacity = 0, IsHitTestVisible = false, Focusable = false, ZIndex = -1 };
        var host = window.WarmupHost;
        host.Children.Add(view);
        try {
            view.UpdateLayout();
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(_ => frame.TrySetResult());
            await Task.WhenAny(frame.Task, Task.Delay(FrameLimit, time));
        } finally {
            host.Children.Remove(view);
            view.DataContext = null;
        }
    }
}
