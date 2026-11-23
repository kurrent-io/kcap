using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

/// The text-size setting for every window without a scale host of its own (About, Feedback, Sign in,
/// the update and consent prompts…). Installed once per application: each such window's content is
/// wrapped in a scale host before its first layout, and a fixed-size window grows with the scale,
/// up to its screen's work area.
public static class SecondaryWindowScale {
    static bool _installed;

    public static void Install(UiScaleSetting setting) {
        if (_installed) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>((window, _) => Attach(window, setting, installed: true));
    }

    // MainWindow and SettingsWindow apply the scale through their own ScaleHost; these have none.
    static readonly HashSet<Type> Scaled = [
        typeof(AboutWindow), typeof(ConsentPromptWindow), typeof(FeedbackWindow), typeof(InstallLocationWindow),
        typeof(LaunchApprovalsWindow), typeof(LifecyclePromptWindow), typeof(SignInWindow), typeof(UninstallWindow),
        typeof(UpdateCheckWindow), typeof(UpdateFinishWindow),
    ];

    static void Attach(Window window, UiScaleSetting setting, bool installed) {
        if (installed && !Scaled.Contains(window.GetType())) return;
        Attach(window, setting);
    }

    public static void Attach(Window window, UiScaleSetting setting) {
        if (window.Content is not Control content || content is LayoutTransformControl { Name: HostName }) return;

        window.Content = null;
        var host = new LayoutTransformControl { Name = HostName, Child = content };
        window.Content = host;

        var applied = UiScale.Default;
        var subscription = setting.Changes
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(scale => {
                host.LayoutTransform = scale == UiScale.Default ? null : new ScaleTransform(scale, scale);
                Resize(window, scale / applied);
                applied = scale;
            });
        window.Closed += (_, _) => subscription.Dispose();
    }

    const string HostName = "SecondaryScaleHost";

    static void Resize(Window window, double factor) {
        if (factor == 1) return;
        var area = (window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary) is { } screen
            ? screen.WorkingArea.Size.ToSize(screen.Scaling)
            : (Size?)null;
        if (window.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && double.IsFinite(window.Width))
            window.Width = Math.Min(window.Width * factor, area?.Width ?? double.MaxValue);
        if (window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && double.IsFinite(window.Height))
            window.Height = Math.Min(window.Height * factor, area?.Height ?? double.MaxValue);
        if (window.MinWidth > 0) window.MinWidth = Math.Min(window.MinWidth * factor, area?.Width ?? double.MaxValue);
        if (window.MinHeight > 0) window.MinHeight = Math.Min(window.MinHeight * factor, area?.Height ?? double.MaxValue);
    }
}
